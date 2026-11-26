using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Email;
using EfMailAgent.Hosting;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Processing;
using EfMailAgent.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Cli;

public sealed record TestRuleOptions(string RuleId, string File, string? Subject, string? From, bool Write, bool NewMessageId);

/// <summary>
/// test-rule: without --write prints prompt, raw answer and parsed rows and writes nothing; with --write the email
/// goes through the queue and the real pipeline (CSV included).
/// </summary>
internal static class TestRuleCommand
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(AgentContext context, TestRuleOptions options, TextWriter output, TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(options.File))
        {
            error.WriteLine($"A fájl nem található: {options.File}");
            return ExitCodes.UsageError;
        }

        var services = new ServiceCollection()
            .AddSingleton(context.LoggerFactory)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddEfMailAgentCore(context.Settings, startRuleWatcher: false);
        await using var provider = services.BuildServiceProvider();

        var ruleSet = provider.GetRequiredService<RuleRepository>().Current;
        var rule = ruleSet.FindById(options.RuleId);
        if (rule is null)
        {
            error.WriteLine($"Nincs ilyen érvényes szabály: '{options.RuleId}'. Érvényes szabályok: {string.Join(", ", ruleSet.Rules.Select(r => r.Id))}");
            if (ruleSet.InvalidFolders.Contains(options.RuleId))
                error.WriteLine("A szabály hibás, részletek: EfMailAgent validate-rules");
            return ExitCodes.UsageError;
        }
        if (!rule.Enabled)
            error.WriteLine($"Figyelem: a(z) '{rule.Id}' szabály ki van kapcsolva; a teszt ettől még lefut.");

        var email = EmailFile.Load(options.File);
        var subject = options.Subject ?? email.Subject;
        var from = options.From ?? email.From;

        return options.Write
            ? await RunWithQueueAsync(provider, rule, email, subject, from, options, output, error, cancellationToken)
            : await RunDryAsync(provider, rule, ruleSet, email, subject, from, output, error, cancellationToken);
    }

    private static async Task<int> RunDryAsync(ServiceProvider provider, Rule rule, RuleSet ruleSet, EmailFileContent email, string subject,
        string from, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var processor = provider.GetRequiredService<JobProcessor>();
        var input = new PromptInput(from, subject, email.ReceivedAt, email.BodyText, email.AttachmentNames);

        ExtractionResult result;
        try
        {
            result = await processor.ExtractAsync(rule, ruleSet.BaseSystemPrompt, input, job: null, cancellationToken);
        }
        catch (LlmTransportException ex)
        {
            error.WriteLine($"Az LLM hívás sikertelen ({processor.Llm.Provider}): {ex.Message}");
            return ExitCodes.ProcessingFailed;
        }

        output.WriteLine(result.Prompt.ToDisplayString());
        Section(output, $"NYERS VÁLASZ ({processor.Llm.Provider}, {result.Duration.TotalSeconds:0.0} s)");
        output.WriteLine(PrettyOrRaw(result.RawResponse));
        if (result.RepairResponse is not null)
        {
            Section(output, "VALIDÁLÁSI HIBÁK AZ ELSŐ VÁLASZBAN");
            foreach (var e in result.FirstValidation.Errors)
                output.WriteLine($"- {e}");
            Section(output, "JAVÍTÓ HÍVÁS VÁLASZA");
            output.WriteLine(PrettyOrRaw(result.RepairResponse));
        }

        var final = result.Final;
        Section(output, "EREDMÉNY");
        if (!final.IsValid)
        {
            output.WriteLine("A válasz érvénytelen (a feladat 'failed' állapotba kerülne):");
            foreach (var e in final.Errors)
                output.WriteLine($"- {e}");
            return ExitCodes.ProcessingFailed;
        }

        var status = final.Rows.Count == 0 || final.Rows.Any(r => r.Status == RowStatus.NeedsReview) ? JobStatus.NeedsReview : JobStatus.Done;
        output.WriteLine($"Érvényes válasz, {final.Rows.Count} sor, a feladat állapota ez lenne: {status}. (Nem íródott CSV.)");
        PrintRows(output, rule, final.Rows);
        return ExitCodes.Ok;
    }

    private static async Task<int> RunWithQueueAsync(ServiceProvider provider, Rule rule, EmailFileContent email, string subject, string from,
        TestRuleOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var queue = provider.GetRequiredService<JobQueue>();
        var processor = provider.GetRequiredService<JobProcessor>();

        var messageId = options.NewMessageId || email.MessageId is null ? MessageIds.Generate("test-rule") : email.MessageId;
        var (job, created) = queue.Enqueue(new NewJob
        {
            MessageId = messageId,
            Account = "cli",
            Folder = Path.GetFileName(options.File),
            ReceivedAt = email.ReceivedAt ?? DateTimeOffset.Now,
            From = from,
            To = email.To,
            Subject = subject,
            BodyText = email.BodyText,
            BodyHtml = email.BodyHtml,
            AttachmentNames = email.AttachmentNames,
            Source = JobSource.Manual,
            RuleId = rule.Id,
        });

        if (!created)
        {
            output.WriteLine($"Ez a Message-ID már szerepel a sorban ({job.MessageId}): feladat {job.Id}, állapot {job.Status}.");
            output.WriteLine("Új feldolgozáshoz használd a --new-message-id kapcsolót.");
            return ExitCodes.Ok;
        }

        output.WriteLine($"Feladat a sorban: {job.Id} ({job.MessageId})");
        var dequeued = queue.TryDequeue(job.Id);
        if (dequeued is null)
        {
            output.WriteLine("A feladatot egy futó szolgáltatás már átvette; állapota az API-n vagy a naplóban követhető.");
            return ExitCodes.Ok;
        }

        var result = await processor.ProcessAsync(dequeued, cancellationToken);
        var final = queue.Get(job.Id)!;
        output.WriteLine($"Állapot: {final.Status}, sorok: {final.RowCount?.ToString() ?? "-"}, kísérletek: {final.Attempts}");
        if (final.OutputFile is not null)
            output.WriteLine($"CSV: {final.OutputFile}");
        if (final.LastError is not null)
            output.WriteLine($"Megjegyzés / hiba: {final.LastError}");
        if (result.Result == AttemptResult.RetryScheduled)
            output.WriteLine("Az újrapróbálást a futó szolgáltatás végzi el.");
        if (result.Result == AttemptResult.PausedForOutage)
            output.WriteLine("Az LLM nem érhető el; a feladat a sorban maradt, a szolgáltatás később feldolgozza.");

        return final.Status == JobStatus.Failed ? ExitCodes.ProcessingFailed : ExitCodes.Ok;
    }

    private static void PrintRows(TextWriter output, Rule rule, IReadOnlyList<ExtractedRow> rows)
    {
        var width = rule.Output.Columns.Max(c => c.Name.Length);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            output.WriteLine();
            output.WriteLine($"{i + 1}. sor  [{row.Status}]{(row.ReviewReasons.Count > 0 ? "  " + string.Join(" ", row.ReviewReasons) : "")}");
            foreach (var column in rule.Output.Columns)
            {
                var value = row.Values[column.Name];
                var text = value is null ? "–" : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
                output.WriteLine($"  {column.Name.PadRight(width)}  {text}");
            }
        }
    }

    private static void Section(TextWriter output, string title)
    {
        output.WriteLine();
        output.WriteLine($"===== {title} =====");
    }

    private static string PrettyOrRaw(string text)
    {
        try
        {
            return JsonNode.Parse(text)?.ToJsonString(PrettyJson) ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }
}
