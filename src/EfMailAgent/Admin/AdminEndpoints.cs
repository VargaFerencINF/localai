using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Addon;
using EfMailAgent.Api;
using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Processing;
using EfMailAgent.Rules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Admin;

public sealed record LoginRequest(string? Token);
public sealed record FileSaveRequest(string? Content);
public sealed record EnabledRequest(bool Enabled);

public sealed record OutputRequest(string? Mode, string? File);
public sealed record SkillRequest(string? FileName, string? Content);
public sealed record CreateRuleRequest(string? Id, string? Name, string? CopyFrom);
public sealed record TestRequest(string? RuleId, string? Subject, string? From, string? Body, DateTimeOffset? ReceivedAt);
public sealed record LlmTestRequest(string? Provider, string? BaseUrl, string? MockDir);

public sealed record AdminJobListItem(string Id, string Status, string RuleId, string? Subject, string? Sender, string Source, int Attempts,
    int? RowCount, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, long? DurationMs);

/// <summary>JSON endpoints of the admin UI under /admin/api (cookie session + CSRF, see <see cref="AdminGuardMiddleware"/>).</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapEfMailAgentAdmin(this IEndpointRouteBuilder app)
    {
        app.MapAdminStaticAssets();

        // One-time login link (opened by the exe on start and by open-admin): sets the session cookie, then the UI.
        app.MapGet("/admin/login", (HttpContext http, string? code, string? page, AdminSessions sessions, LoginCodeRegistry codes,
            AppSettings settings, ILoggerFactory loggers) =>
        {
            var log = loggers.CreateLogger("EfMailAgent.Admin");
            if (!codes.TryConsume(settings.Server.Token, code))
            {
                log.LogWarning("Lejárt vagy már felhasznált bejelentkező link.");
                return Results.Redirect("/admin/?link=lejart");
            }
            log.LogInformation("Admin bejelentkezés (indítási link).");
            http.Response.Cookies.Append(AdminSessions.CookieName, sessions.Issue(), AdminSessions.CookieOptions());
            return Results.Redirect($"/admin/#/{(page is not null && LoginLink.Pages.Contains(page) ? page : "attekintes")}");
        }).ExcludeFromDescription();
        var api = app.MapGroup("/admin/api").ExcludeFromDescription().AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (AdminRequestException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.StatusCode);
            }
        });

        // ---- session ----
        api.MapGet("/session", (HttpContext http, AdminSessions sessions) =>
            Results.Ok(new { authenticated = sessions.IsValid(http.Request.Cookies[AdminSessions.CookieName]), version = VersionInfo.Current }));

        api.MapPost("/login", async (HttpContext http, LoginRequest request, AdminSessions sessions, ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("EfMailAgent.Admin");
            if (!sessions.TokenMatches(request.Token?.Trim()))
            {
                logger.LogWarning("Sikertelen admin bejelentkezés.");
                await Task.Delay(500); // slows down guessing
                return Results.Json(new ErrorResponse("Hibás token."), statusCode: StatusCodes.Status401Unauthorized);
            }
            http.Response.Cookies.Append(AdminSessions.CookieName, sessions.Issue(), AdminSessions.CookieOptions());
            logger.LogInformation("Admin bejelentkezés.");
            return Results.Ok(new { authenticated = true });
        });

        api.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(AdminSessions.CookieName, AdminSessions.CookieOptions());
            return Results.Ok(new { authenticated = false });
        });

        // ---- overview & jobs ----
        api.MapGet("/overview", async (LlmHealthCache health, JobQueue queue, RuleRepository rules, EffectiveSettings settings, JobWorker worker,
            CancellationToken cancellationToken) =>
        {
            var llm = await health.GetAsync(cancellationToken);
            var set = rules.Current;
            return Results.Ok(new
            {
                version = VersionInfo.Current,
                dataDir = settings.DataDirectory.Path,
                outputDir = settings.OutputDir,
                llm = new
                {
                    provider = settings.Settings.Llm.Provider,
                    model = settings.Settings.Llm.Model,
                    baseUrl = settings.Settings.Llm.BaseUrl,
                    reachable = llm.Reachable,
                    error = llm.Error,
                    outage = worker.InOutage,
                },
                queueLength = queue.QueueLength(),
                counts = queue.CountByStatus(),
                rules = new
                {
                    version = set.Version,
                    valid = set.Rules.Count,
                    enabled = set.EnabledRules.Count(),
                    invalid = set.InvalidFolders,
                    errors = set.Issues.Count(i => !i.IsWarning),
                },
                addon = AddonSummary(settings),
            });
        });

        api.MapGet("/jobs", (JobQueue queue, string? status, int? limit) =>
        {
            if (!string.IsNullOrWhiteSpace(status) && !JobStatus.All.Contains(status))
                return Results.BadRequest(new ErrorResponse($"Ismeretlen állapot: {status}"));
            var jobs = queue.List(string.IsNullOrWhiteSpace(status) ? null : status, Math.Clamp(limit ?? 50, 1, 500));
            return Results.Ok(jobs.Select(j => new AdminJobListItem(j.Id, j.Status, j.RuleId, j.Subject, j.Sender, j.Source, j.Attempts, j.RowCount,
                j.LastError, j.CreatedAt, j.FinishedAt, j.DurationMs)));
        });

        api.MapGet("/jobs/{id}", (string id, JobQueue queue) =>
        {
            var job = queue.Get(id);
            if (job is null)
                return Results.NotFound(new ErrorResponse("A feladat nem található."));
            JsonNode? rows = null;
            try
            {
                rows = job.ParsedRows is null ? null : JsonNode.Parse(job.ParsedRows);
            }
            catch (JsonException)
            {
            }
            return Results.Ok(new
            {
                job.Id, job.Status, job.RuleId, job.MessageId, job.Account, job.Folder, job.ReceivedAt, job.Sender, job.Recipients, job.Subject,
                job.AttachmentNames, job.Source, job.Attempts, job.NextAttemptAt, error = job.LastError, job.OutputFile, job.RowCount,
                job.CreatedAt, job.StartedAt, job.FinishedAt, job.DurationMs, job.OriginalSender, job.PromptSystem, job.PromptUser,
                job.RawResponse, job.RepairResponse, parsedRows = rows,
                bodyText = job.BodyText is { Length: > 50_000 } body ? body[..50_000] + "\n[…]" : job.BodyText,
            });
        });

        api.MapPost("/jobs/{id}/retry", (string id, JobQueue queue) =>
        {
            var job = queue.Get(id);
            if (job is null)
                return Results.NotFound(new ErrorResponse("A feladat nem található."));
            return queue.Requeue(id)
                ? Results.Ok(new { id, status = JobStatus.Queued })
                : Results.Conflict(new ErrorResponse($"Csak hibás vagy ellenőrzendő feladat futtatható újra (most: {job.Status})."));
        });

        // ---- rules ----
        api.MapGet("/rules", (RuleFileService files) => Results.Ok(files.List()));

        api.MapGet("/rules/{folder}/file", (string folder, string path, RuleFileService files) =>
            Results.Ok(new { folder, path, content = files.Read(folder, path) }));

        api.MapPut("/rules/{folder}/file", (string folder, string path, FileSaveRequest request, RuleFileService files) =>
            SaveResponse(files.Save(folder, path, request.Content ?? "")));

        api.MapPost("/rules/{folder}/enabled", (string folder, EnabledRequest request, RuleFileService files) =>
            SaveResponse(files.SetEnabled(folder, request.Enabled)));

        api.MapPost("/rules/{folder}/output", (string folder, OutputRequest request, RuleFileService files) =>
            SaveResponse(files.SetOutput(folder, request.Mode ?? "", request.File ?? "")));

        api.MapPost("/rules/{folder}/skills", (string folder, SkillRequest request, RuleFileService files) =>
            SaveResponse(files.AddSkill(folder, request.FileName?.Trim() ?? "", request.Content ?? "")));

        api.MapDelete("/rules/{folder}/file", (string folder, string path, RuleFileService files) =>
            SaveResponse(files.RemoveSkill(folder, path)));

        api.MapPost("/rules", (CreateRuleRequest request, RuleFileService files) =>
            SaveResponse(files.Create(request.Id ?? "", request.Name ?? "", string.IsNullOrWhiteSpace(request.CopyFrom) ? null : request.CopyFrom)));

        // ---- test console ----
        api.MapGet("/samples", () => Results.Ok(SampleEmails.Load()));

        api.MapPost("/test", async (TestRequest request, RuleRepository rules, JobProcessor processor, EffectiveSettings settings, TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var set = rules.Reload(); // include changes saved a moment ago
            var rule = set.FindById(request.RuleId ?? "");
            if (rule is null)
                return Results.NotFound(new ErrorResponse($"Nincs ilyen érvényes szabály: '{request.RuleId}'."));

            var input = new PromptInput(request.From, request.Subject, request.ReceivedAt ?? time.GetUtcNow(), request.Body, []);
            var stopwatch = Stopwatch.StartNew();
            var columns = rule.Output.Columns.Select(c => new { c.Name, type = c.Type.ToString().ToLowerInvariant(), c.Required, c.Description });
            var model = rule.Llm.Model ?? settings.Settings.Llm.Model;
            try
            {
                var result = await processor.ExtractAsync(rule, set.BaseSystemPrompt, input, job: null, cancellationToken);
                var final = result.Final;
                var status = !final.IsValid ? JobStatus.Failed
                    : final.Rows.Count == 0 || final.Rows.Any(r => r.Status == RowStatus.NeedsReview) ? JobStatus.NeedsReview : JobStatus.Done;
                return Results.Ok(new
                {
                    ok = true,
                    provider = processor.Llm.Provider,
                    model,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    prompt = new { system = result.Prompt.System, user = result.Prompt.User },
                    originalSender = result.Prompt.OriginalSender,
                    bodyTruncated = result.Prompt.BodyTruncated,
                    rawResponse = result.RawResponse,
                    repairResponse = result.RepairResponse,
                    firstErrors = result.FirstValidation.Errors,
                    finalErrors = final.Errors,
                    valid = final.IsValid,
                    status,
                    rows = final.Rows.Select(r => new { status = r.Status, reasons = r.ReviewReasons, values = r.Values }),
                    columns,
                });
            }
            catch (LlmTransportException ex)
            {
                return Results.Ok(new { ok = false, provider = processor.Llm.Provider, model, durationMs = stopwatch.ElapsedMilliseconds, transportError = ex.Message, columns });
            }
        });

        // ---- settings ----
        api.MapGet("/settings", (SettingsService service) => Results.Ok(service.Get()));

        api.MapPut("/settings", (SettingsUpdate update, SettingsService service) =>
        {
            var result = service.Save(update);
            return result.Saved ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        api.MapPost("/settings/test-llm", async (LlmTestRequest request, EffectiveSettings settings, CancellationToken cancellationToken) =>
        {
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings.Settings))!;
            copy.Llm.Provider = request.Provider ?? copy.Llm.Provider;
            copy.Llm.BaseUrl = request.BaseUrl ?? copy.Llm.BaseUrl;
            copy.Llm.MockDir = request.MockDir ?? copy.Llm.MockDir;
            if (!LlmProviders.All.Contains(copy.Llm.Provider))
                return Results.BadRequest(new ErrorResponse($"Ismeretlen szolgáltató: {copy.Llm.Provider}"));
            if (copy.Llm.Provider != LlmProviders.Mock && !Uri.TryCreate(copy.Llm.BaseUrl, UriKind.Absolute, out _))
                return Results.BadRequest(new ErrorResponse($"Érvénytelen URL: {copy.Llm.BaseUrl}"));
            var client = LlmClientFactory.Create(new EffectiveSettings(settings.DataDirectory, copy));
            var health = await client.CheckHealthAsync(cancellationToken);
            return Results.Ok(new { health.Reachable, health.Error, health.Models, durationMs = (long)health.Duration.TotalMilliseconds });
        });

        // ---- log ----
        api.MapGet("/logs", (EffectiveSettings settings, string? level, int? lines) =>
            Results.Ok(LogTail.Read(settings.LogsDir, string.IsNullOrWhiteSpace(level) ? "INF" : level, lines ?? 300)));

        return app;
    }

    private static IResult SaveResponse(SaveResult result) =>
        result.Saved ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static object AddonSummary(EffectiveSettings settings)
    {
        try
        {
            var status = AddonSelfHealService.CreateInstaller(settings).GetStatus();
            return new
            {
                ok = status.Ok,
                mode = status.State?.Mode,
                bundledVersion = status.Bundled?.Version,
                installedVersion = status.InstalledXpi?.Version,
                thunderbirdRunning = status.ThunderbirdRunning,
                installations = status.Installations.Select(i => new
                {
                    dir = i.Installation.InstallDir,
                    version = i.Installation.Version,
                    entry = i.Entry is not null,
                    ok = i.Ok,
                    tokenOk = i.TokenOk,
                    urlOk = i.InstallUrlOk,
                }),
                error = status.BundledError,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message, installations = Array.Empty<object>() };
        }
    }
}
