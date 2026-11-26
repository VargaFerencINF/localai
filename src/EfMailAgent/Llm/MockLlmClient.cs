using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EfMailAgent.Llm;

/// <summary>
/// Simulated LLM for development without a model (docs/DEV_MODE.md section 2). Only the HTTP call is replaced: the
/// answer goes through the same parsing, validation, repair and CSV path as a real one.
/// Lookup: &lt;MockDir&gt;/&lt;ruleId&gt;/by-message-id/&lt;sanitized Message-ID&gt;.json, then &lt;MockDir&gt;/&lt;ruleId&gt;/default.json.
/// Subject tokens [mock:invalid], [mock:invalid-then-valid], [mock:empty], [mock:timeout] select error scenarios.
/// </summary>
public sealed partial class MockLlmClient(string mockDir, int delayMs, string? promptDumpDir) : ILlmClient
{
    public const string ScenarioInvalid = "[mock:invalid]";
    public const string ScenarioInvalidThenValid = "[mock:invalid-then-valid]";
    public const string ScenarioEmpty = "[mock:empty]";
    public const string ScenarioTimeout = "[mock:timeout]";

    [GeneratedRegex("[^A-Za-z0-9._@-]")]
    private static partial Regex UnsafeFileChars();

    public string Provider => "mock";

    public string MockDir { get; } = mockDir;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        DumpPrompt(request);

        var subject = request.Subject ?? "";
        if (subject.Contains(ScenarioTimeout, StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(request.Timeout, cancellationToken);
            throw new LlmTransportException(LlmErrorKind.Timeout,
                $"Az LLM nem válaszolt {request.Timeout.TotalSeconds:0} másodpercen belül (időtúllépés, mock).");
        }

        if (delayMs > 0)
            await Task.Delay(delayMs, cancellationToken);

        string content;
        if (subject.Contains(ScenarioInvalidThenValid, StringComparison.OrdinalIgnoreCase))
            content = request.IsRepair ? LookupNormal(request) : ReadScenario("invalid.json");
        else if (subject.Contains(ScenarioInvalid, StringComparison.OrdinalIgnoreCase))
            content = ReadScenario("invalid.json");
        else if (subject.Contains(ScenarioEmpty, StringComparison.OrdinalIgnoreCase))
            content = "{\"rows\": []}";
        else
            content = LookupNormal(request);

        return new LlmResponse(content, stopwatch.Elapsed, "mock");
    }

    public Task<LlmHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var ok = Directory.Exists(MockDir);
        return Task.FromResult(new LlmHealth(ok, Provider, ok ? null : $"A mock válaszok mappája nem létezik: {MockDir}",
            ok ? ["mock"] : [], TimeSpan.Zero));
    }

    public static string SanitizeMessageId(string messageId) =>
        UnsafeFileChars().Replace(messageId.Trim().Trim('<', '>'), "_");

    private string LookupNormal(LlmRequest request)
    {
        var ruleDir = Path.Combine(MockDir, request.RuleId ?? "_none");
        if (!string.IsNullOrWhiteSpace(request.MessageId))
        {
            var byId = Path.Combine(ruleDir, "by-message-id", SanitizeMessageId(request.MessageId) + ".json");
            if (File.Exists(byId))
                return File.ReadAllText(byId);
        }

        var fallback = Path.Combine(ruleDir, "default.json");
        if (File.Exists(fallback))
            return File.ReadAllText(fallback);

        throw new LlmTransportException(LlmErrorKind.Other,
            $"nincs mock válasz: sem {Path.Combine(ruleDir, "by-message-id")}, sem {fallback} nem található.");
    }

    private string ReadScenario(string file)
    {
        var path = Path.Combine(MockDir, "_scenarios", file);
        if (!File.Exists(path))
            throw new LlmTransportException(LlmErrorKind.Other, $"nincs mock válasz: {path} nem található.");
        return File.ReadAllText(path);
    }

    /// <summary>Writes the fully assembled prompt to data/mock-prompts/&lt;jobId&gt;.txt (one section per call).</summary>
    private void DumpPrompt(LlmRequest request)
    {
        if (promptDumpDir is null || string.IsNullOrWhiteSpace(request.JobId))
            return;

        Directory.CreateDirectory(promptDumpDir);
        var sb = new StringBuilder();
        sb.Append($"##### {(request.IsRepair ? "Javító hívás" : "Első hívás")} ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}) #####\n");
        foreach (var message in request.Messages)
            sb.Append($"===== {message.Role.ToUpperInvariant()} =====\n{message.Content}\n\n");

        var path = Path.Combine(promptDumpDir, UnsafeFileChars().Replace(request.JobId, "_") + ".txt");
        if (request.IsRepair)
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        else
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
