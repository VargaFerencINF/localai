using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Output;
using EfMailAgent.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfMailAgent.Processing;

/// <summary>What happened with one processing attempt.</summary>
public enum AttemptResult
{
    Completed,
    RetryScheduled,
    PausedForOutage,
    Released,
}

public sealed record ProcessResult(AttemptResult Result, string? Status, string? Error, LlmTransportException? Transport = null);

/// <summary>Result of running a prompt against the LLM including the optional repair call (no side effects).</summary>
public sealed record ExtractionResult(
    AssembledPrompt Prompt,
    string RawResponse,
    string? RepairResponse,
    ValidationOutcome FirstValidation,
    ValidationOutcome? RepairValidation,
    TimeSpan Duration,
    LlmUsage? Usage = null)
{
    public ValidationOutcome Final => RepairValidation ?? FirstValidation;
}

/// <summary>
/// Processes one dequeued job: prompt, LLM call, validation, one repair call, row statuses, CSV, job completion.
/// Transport errors are turned into retries / outage pauses here; everything else ends the job.
/// </summary>
public sealed class JobProcessor(
    RuleRepository rules,
    JobQueue queue,
    ILlmClient llm,
    CsvWriter csv,
    EffectiveSettings settings,
    TimeZoneInfo timeZone,
    TimeProvider? timeProvider = null,
    ILogger<JobProcessor>? logger = null)
{
    private static readonly JsonSerializerOptions RowsJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public ILlmClient Llm => llm;

    public async Task<ProcessResult> ProcessAsync(Job job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["RuleId"] = job.RuleId });
        _logger.LogInformation("Feldolgozás indul: {JobId}, szabály {RuleId}, {Attempt}. kísérlet", job.Id, job.RuleId, job.Attempts);

        var ruleSet = rules.Current;
        var rule = ruleSet.FindById(job.RuleId);
        if (rule is null || !rule.Enabled)
        {
            var error = rule is null
                ? $"A(z) '{job.RuleId}' szabály nem található (törölték vagy hibás)."
                : $"A(z) '{job.RuleId}' szabály ki van kapcsolva.";
            return Fail(job, error, null, null, stopwatch);
        }

        ExtractionResult extraction;
        try
        {
            extraction = await ExtractAsync(rule, ruleSet.BaseSystemPrompt, ToPromptInput(job), job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            queue.Release(job.Id, "A szolgáltatás leállt feldolgozás közben, a feladat visszakerült a sorba.", null);
            _logger.LogInformation("Leállítás: a(z) {JobId} feladat visszakerült a sorba.", job.Id);
            return new ProcessResult(AttemptResult.Released, JobStatus.Queued, null);
        }
        catch (LlmTransportException ex)
        {
            return HandleTransportError(job, ex);
        }

        var final = extraction.Final;
        if (!final.IsValid)
        {
            var error = "A modell válasza a javító hívás után sem felel meg a sémának: " + string.Join("; ", final.Errors.Take(10));
            return Fail(job, error, extraction.RawResponse, extraction.RepairResponse, stopwatch);
        }

        var rows = final.Rows;
        var status = rows.Count == 0 || rows.Any(r => r.Status == RowStatus.NeedsReview) ? JobStatus.NeedsReview : JobStatus.Done;
        var parsedRows = JsonSerializer.Serialize(rows.Select(r => new { status = r.Status, reasons = r.ReviewReasons, values = r.Values }), RowsJson);

        CsvWriteResult? written;
        try
        {
            written = await csv.WriteAsync(rule, job, rows, _time.GetUtcNow(), extraction.Prompt.OriginalSender, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            queue.Release(job.Id, "A szolgáltatás leállt a CSV írása közben, a feladat visszakerült a sorba.", null);
            return new ProcessResult(AttemptResult.Released, JobStatus.Queued, null);
        }
        catch (CsvWriteException ex)
        {
            // The parsed rows stay in the database (parsed_rows), so nothing is lost; a retry rewrites them.
            queue.Complete(job.Id, new JobCompletion(JobStatus.Failed, ex.Message, extraction.RawResponse, extraction.RepairResponse,
                null, rows.Count, parsedRows, stopwatch.ElapsedMilliseconds, extraction.Usage));
            _logger.LogError("Feldolgozás sikertelen: {JobId}, CSV hiba: {Error}", job.Id, ex.Message);
            return new ProcessResult(AttemptResult.Completed, JobStatus.Failed, ex.Message);
        }

        var note = rows.Count == 0 ? "A modell nem talált egyetlen tételt sem." : written?.IsPending == true
            ? $"A CSV fájl zárolva volt, a sorok ide kerültek: {written.FilePath}"
            : null;
        queue.Complete(job.Id, new JobCompletion(status, note, extraction.RawResponse, extraction.RepairResponse,
            written?.FilePath, rows.Count, parsedRows, stopwatch.ElapsedMilliseconds, extraction.Usage));

        _logger.LogInformation(
            "Feldolgozás kész: {JobId}, szabály {RuleId}, állapot {Status}, {RowCount} sor, {DurationMs} ms, javító hívás: {Repaired}, fájl: {OutputFile}",
            job.Id, rule.Id, status, rows.Count, stopwatch.ElapsedMilliseconds, extraction.RepairResponse is not null ? "igen" : "nem",
            written?.FilePath ?? "-");
        return new ProcessResult(AttemptResult.Completed, status, note);
    }

    /// <summary>
    /// Prompt + LLM call + validation + one repair call. Used by the worker and by test-rule / the admin test console.
    /// Throws <see cref="LlmTransportException"/> when the model gives no answer.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(Rule rule, string baseSystemPrompt, PromptInput input, Job? job,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var prompt = PromptBuilder.Build(rule, baseSystemPrompt, input, _time.GetUtcNow(), timeZone);
        if (job is not null)
            queue.SavePrompt(job.Id, prompt.System, prompt.User, prompt.OriginalSender);
        _logger.LogDebug("Összeállított prompt ({JobId}):\n{Prompt}", job?.Id, prompt.ToDisplayString());

        var schema = RuleSchemaBuilder.Build(rule);
        var request = CreateRequest(rule, prompt.Messages, schema, input, job);
        var first = await llm.CompleteAsync(request, cancellationToken);
        var firstValidation = ResponseValidator.Validate(rule.Output, schema, first.Content);
        if (firstValidation.IsValid)
            return new ExtractionResult(prompt, first.Content, null, firstValidation, null, stopwatch.Elapsed, LlmUsage.From(first));

        _logger.LogWarning("A modell válasza nem felel meg a sémának ({JobId}), javító hívás következik. Hibák: {Errors}",
            job?.Id, string.Join("; ", firstValidation.Errors.Take(5)));

        var repairMessages = new List<LlmMessage>(prompt.Messages)
        {
            new(LlmRoles.Assistant, HttpLlmClientBase.Shorten(first.Content, 20000)),
            new(LlmRoles.User, BuildRepairMessage(firstValidation.Errors)),
        };
        var repair = await llm.CompleteAsync(request with { Messages = repairMessages }, cancellationToken);
        var repairValidation = ResponseValidator.Validate(rule.Output, schema, repair.Content);
        return new ExtractionResult(prompt, first.Content, repair.Content, firstValidation, repairValidation, stopwatch.Elapsed, LlmUsage.From(first, repair));
    }

    public static string BuildRepairMessage(IReadOnlyList<string> errors) =>
        "Az előző válaszod nem felel meg az előírt JSON sémának. A hibák:\n"
        + string.Join("\n", errors.Take(30).Select(e => "- " + e))
        + "\n\nAdd vissza a javított választ: kizárólag a sémának megfelelő JSON objektumot, magyarázat és más szöveg nélkül. "
        + "Ne találj ki új adatot; ami nem szerepel egyértelműen az e-mailben, annak az értéke null.";

    public static PromptInput ToPromptInput(Job job) =>
        new(job.Sender, job.Subject, job.ReceivedAt, job.BodyText, job.AttachmentNames);

    private LlmRequest CreateRequest(Rule rule, IReadOnlyList<LlmMessage> messages, JsonObject schema, PromptInput input, Job? job)
    {
        var llmSettings = settings.Settings.Llm;
        return new LlmRequest
        {
            Messages = messages,
            Schema = schema,
            SchemaName = rule.Id.Replace('-', '_'),
            Model = rule.Llm.Model ?? llmSettings.Model,
            Temperature = rule.Llm.Temperature ?? llmSettings.Temperature,
            NumCtx = llmSettings.NumCtx,
            Think = llmSettings.Think,
            Timeout = TimeSpan.FromSeconds(llmSettings.TimeoutSeconds),
            JobId = job?.Id,
            RuleId = rule.Id,
            Subject = input.Subject,
            MessageId = job?.MessageId,
        };
    }

    private ProcessResult HandleTransportError(Job job, LlmTransportException ex)
    {
        var worker = settings.Settings.Worker;
        if (ex.IsOutage)
        {
            var until = _time.GetUtcNow().AddSeconds(worker.LlmOutagePauseSeconds);
            queue.Release(job.Id, ex.Message, until);
            return new ProcessResult(AttemptResult.PausedForOutage, JobStatus.Queued, ex.Message, ex);
        }

        var delays = worker.EffectiveRetryDelays;
        if (job.Attempts <= delays.Count)
        {
            var delay = delays[job.Attempts - 1];
            var next = _time.GetUtcNow().Add(delay);
            var message = $"{ex.Message} ({job.Attempts}. kísérlet, újrapróbálás {delay.TotalSeconds:0} másodperc múlva)";
            queue.ScheduleRetry(job.Id, message, next);
            _logger.LogWarning("Átmeneti LLM hiba: {JobId}, {Attempt}/{MaxAttempts}. kísérlet, újra: {Next:O}. {Error}",
                job.Id, job.Attempts, worker.MaxAttempts, next, ex.Message);
            return new ProcessResult(AttemptResult.RetryScheduled, JobStatus.Queued, message, ex);
        }

        var error = $"{ex.Message} (a(z) {worker.MaxAttempts}. kísérlet után is sikertelen)";
        queue.Complete(job.Id, new JobCompletion(JobStatus.Failed, error, null, null, null, null, null, null));
        _logger.LogError("Feldolgozás sikertelen: {JobId}, elfogytak a kísérletek. {Error}", job.Id, ex.Message);
        return new ProcessResult(AttemptResult.Completed, JobStatus.Failed, error, ex);
    }

    private ProcessResult Fail(Job job, string error, string? raw, string? repair, Stopwatch stopwatch)
    {
        queue.Complete(job.Id, new JobCompletion(JobStatus.Failed, error, raw, repair, null, null, null, stopwatch.ElapsedMilliseconds));
        _logger.LogError("Feldolgozás sikertelen: {JobId}, szabály {RuleId}: {Error}", job.Id, job.RuleId, error);
        return new ProcessResult(AttemptResult.Completed, JobStatus.Failed, error);
    }
}
