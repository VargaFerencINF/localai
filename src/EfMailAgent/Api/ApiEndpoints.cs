using System.Globalization;
using System.Text.Json;
using EfMailAgent.Configuration;
using EfMailAgent.Email;
using EfMailAgent.Jobs;
using EfMailAgent.Processing;
using EfMailAgent.Rules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Api;

public static class ApiEndpoints
{
    public const string CorsPolicy = "thunderbird-addon";

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapEfMailAgentApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireCors(CorsPolicy).WithTags("EfMailAgent");

        api.MapGet("/health", GetHealthAsync)
            .WithSummary("Állapot: verzió, LLM elérhetőség (30 mp-ig gyorsítótárazva), sorhossz, szabályverzió")
            .Produces<HealthResponse>();

        api.MapGet("/matchers", GetMatchers)
            .WithSummary("A bekapcsolt szabályok tárgyszűrői az add-on számára; ETag / If-None-Match (304) támogatott")
            .Produces<MatchersResponse>()
            .Produces(StatusCodes.Status304NotModified);

        api.MapPost("/jobs", SubmitAsync)
            .WithSummary("E-mail beküldése feldolgozásra (azonnal visszatér, nem vár az LLM-re)")
            .Accepts<SubmitJobRequest>("application/json")
            .Produces<SubmitJobResponse>(StatusCodes.Status202Accepted)
            .Produces<SubmitJobResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        api.MapGet("/jobs/{id}", GetJob)
            .WithSummary("Egy feladat állapota")
            .Produces<JobDto>()
            .Produces(StatusCodes.Status404NotFound);

        api.MapGet("/jobs", ListJobs)
            .WithSummary("Feladatok listája (legújabb elöl), opcionális állapotszűrővel")
            .Produces<JobListResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        api.MapPost("/jobs/{id}/retry", Retry)
            .WithSummary("Hibás vagy ellenőrzendő feladat újra sorba állítása")
            .Produces<JobDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetHealthAsync(LlmHealthCache healthCache, JobQueue queue, RuleRepository rules,
        AppSettings settings, JobWorker worker, CancellationToken cancellationToken)
    {
        var health = await healthCache.GetAsync(cancellationToken);
        var set = rules.Current;
        var status = health.Reachable && set.InvalidFolders.Count == 0 ? "ok" : "degraded";
        return Results.Ok(new HealthResponse(status, VersionInfo.Current, health.Reachable, queue.QueueLength(), set.Version,
            settings.Llm.Provider, string.IsNullOrWhiteSpace(settings.Llm.Model) ? null : settings.Llm.Model, health.Error,
            worker.InOutage, set.Rules.Count, set.InvalidFolders.Count));
    }

    private static IResult GetMatchers(HttpContext context, RuleRepository rules)
    {
        var set = rules.Current;
        var etag = $"\"{set.Version}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";

        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (ifNoneMatch.Split(',').Select(v => v.Trim()).Any(v => v == etag || v == "*" || v == "W/" + etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var matchers = set.EnabledRules
            .OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new MatcherDto(r.Id, r.Name, r.SubjectContains, r.SubjectRegex, r.Priority))
            .ToArray();
        return Results.Ok(new MatchersResponse(set.Version, matchers));
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, JobQueue queue, RuleRepository rules, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("EfMailAgent.Api");

        SubmitJobRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<SubmitJobRequest>(context.Request.Body, RequestJson, context.RequestAborted);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new ErrorResponse($"A kérés törzse nem érvényes JSON: {ex.Message}"));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (request is null)
            return Results.BadRequest(new ErrorResponse("A kérés törzse üres."));

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.MessageId) || MessageIds.Canonicalize(request.MessageId).Length == 0)
            errors["messageId"] = ["A messageId megadása kötelező."];
        if (string.IsNullOrWhiteSpace(request.BodyText) && string.IsNullOrWhiteSpace(request.BodyHtml))
            errors["bodyText"] = ["A bodyText megadása kötelező (vagy a bodyHtml, ha nincs szöveges rész)."];
        var source = string.IsNullOrWhiteSpace(request.Source) ? JobSource.Auto : request.Source.Trim();
        if (source is not (JobSource.Auto or JobSource.Manual))
            errors["source"] = [$"Ismeretlen source: '{request.Source}' (megengedett: auto, manual)."];
        DateTimeOffset? receivedAt = null;
        if (!string.IsNullOrWhiteSpace(request.ReceivedAt))
        {
            if (DateTimeOffset.TryParse(request.ReceivedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                receivedAt = parsed;
            else
                errors["receivedAt"] = [$"Érvénytelen dátum: '{request.ReceivedAt}' (ISO 8601 formátum kell)."];
        }
        string? to = null;
        if (request.To is { } toElement)
        {
            switch (toElement.ValueKind)
            {
                case JsonValueKind.String:
                    to = toElement.GetString();
                    break;
                case JsonValueKind.Array when toElement.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String):
                    to = string.Join(", ", toElement.EnumerateArray().Select(e => e.GetString()));
                    break;
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    break;
                default:
                    errors["to"] = ["A 'to' szöveg vagy szövegek tömbje lehet."];
                    break;
            }
        }
        if (errors.Count > 0)
            return Results.BadRequest(new ErrorResponse("Hibás kérés.", errors));

        // Dedup first: a re-submitted message always gets its existing job, whatever the rules say now.
        var existing = queue.GetByMessageId(request.MessageId!);
        if (existing is not null)
            return Results.Ok(new SubmitJobResponse(existing.Id, existing.RuleId, existing.Status, Duplicate: true));

        var set = rules.Current;
        Rule? rule;
        if (!string.IsNullOrWhiteSpace(request.RuleId))
        {
            rule = set.FindById(request.RuleId.Trim());
            if (rule is null || !rule.Enabled)
                return Results.UnprocessableEntity(new ErrorResponse($"Ismeretlen vagy kikapcsolt szabály: '{request.RuleId}'."));
        }
        else
        {
            rule = set.FindBestMatch(request.Subject);
            if (rule is null)
                return Results.UnprocessableEntity(new ErrorResponse("A tárgy egyik bekapcsolt szabályra sem illeszkedik, és nincs megadva ruleId."));
        }

        var bodyText = string.IsNullOrWhiteSpace(request.BodyText) ? HtmlToText.Convert(request.BodyHtml) : request.BodyText;
        var (job, created) = queue.Enqueue(new NewJob
        {
            MessageId = request.MessageId!,
            Account = request.Account,
            Folder = request.Folder,
            ReceivedAt = receivedAt,
            From = request.From,
            To = to,
            Subject = request.Subject,
            BodyText = bodyText,
            BodyHtml = request.BodyHtml,
            AttachmentNames = request.AttachmentNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? [],
            Source = source,
            RuleId = rule.Id,
        });

        if (!created) // a concurrent submission won the race
            return Results.Ok(new SubmitJobResponse(job.Id, job.RuleId, job.Status, Duplicate: true));

        logger.LogInformation("Új feladat: {JobId}, szabály {RuleId}, forrás {Source}, Message-ID {MessageId}, tárgy: {Subject}",
            job.Id, job.RuleId, job.Source, job.MessageId, job.Subject);
        return Results.Json(new SubmitJobResponse(job.Id, job.RuleId, job.Status, Duplicate: false), statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult GetJob(string id, JobQueue queue) =>
        queue.Get(id) is { } job ? Results.Ok(JobDto.From(job)) : Results.NotFound();

    private static IResult ListJobs(JobQueue queue, string? status, int? limit)
    {
        if (!string.IsNullOrWhiteSpace(status) && !JobStatus.All.Contains(status))
            return Results.BadRequest(new ErrorResponse($"Ismeretlen állapot: '{status}' (megengedett: {string.Join(", ", JobStatus.All)})."));
        var jobs = queue.List(string.IsNullOrWhiteSpace(status) ? null : status, Math.Clamp(limit ?? 50, 1, 500));
        return Results.Ok(new JobListResponse(jobs.Select(JobDto.From).ToArray()));
    }

    private static IResult Retry(string id, JobQueue queue)
    {
        var job = queue.Get(id);
        if (job is null)
            return Results.NotFound();
        if (!queue.Requeue(id))
            return Results.Conflict(new ErrorResponse($"Csak hibás vagy ellenőrzendő feladat futtatható újra (jelenlegi állapot: {job.Status})."));
        return Results.Ok(JobDto.From(queue.Get(id)!));
    }
}
