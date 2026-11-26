using System.Text.Json.Nodes;

namespace EfMailAgent.Llm;

public static class LlmRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
}

public sealed record LlmMessage(string Role, string Content);

/// <summary>One chat completion request. JobId/RuleId/Subject/MessageId are context (used by the mock provider).</summary>
public sealed record LlmRequest
{
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public required JsonObject Schema { get; init; }
    public required string SchemaName { get; init; }
    public string Model { get; init; } = "";
    public double Temperature { get; init; }
    public int NumCtx { get; init; }
    public bool Think { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(300);

    public string? JobId { get; init; }
    public string? RuleId { get; init; }
    public string? Subject { get; init; }
    public string? MessageId { get; init; }

    /// <summary>True for the repair call (the request contains the invalid answer and the validation errors).</summary>
    public bool IsRepair => Messages.Count > 2;
}

public sealed record LlmResponse(string Content, TimeSpan Duration, string? Model = null, int? PromptTokens = null, int? CompletionTokens = null,
    double? TokensPerSecond = null);

/// <summary>Token counts and generation speed of the LLM call(s) of one job (Teljesítmény page, job details).</summary>
public sealed record LlmUsage(int? PromptTokens, int? OutputTokens, double? TokensPerSecond, string? Model)
{
    /// <summary>Sums the calls; the speed is weighted by the output tokens of each call.</summary>
    public static LlmUsage? From(params LlmResponse?[] responses)
    {
        var calls = responses.OfType<LlmResponse>().ToList();
        if (calls.Count == 0)
            return null;
        int? Sum(Func<LlmResponse, int?> pick) => calls.Any(c => pick(c) is not null) ? calls.Sum(c => pick(c) ?? 0) : null;
        var timed = calls.Where(c => c.CompletionTokens is > 0 && c.TokensPerSecond is > 0).ToList();
        double? speed = timed.Count == 0 ? null
            : timed.Sum(c => (double)c.CompletionTokens!.Value) / timed.Sum(c => c.CompletionTokens!.Value / c.TokensPerSecond!.Value);
        return new LlmUsage(Sum(c => c.PromptTokens), Sum(c => c.CompletionTokens), speed is null ? null : Math.Round(speed.Value, 1),
            calls.Select(c => c.Model).LastOrDefault(m => m is not null));
    }
}

public sealed record LlmHealth(bool Reachable, string Provider, string? Error, IReadOnlyList<string> Models, TimeSpan Duration);

public enum LlmErrorKind
{
    /// <summary>Server not reachable (connection refused, DNS). The worker pauses; job attempts are not used up.</summary>
    Unreachable,

    /// <summary>Configuration problem (e.g. no model set). Treated like <see cref="Unreachable"/>.</summary>
    Configuration,

    /// <summary>No answer within the configured timeout. Counts as an attempt.</summary>
    Timeout,

    /// <summary>HTTP 5xx. Counts as an attempt.</summary>
    ServerError,

    /// <summary>HTTP 4xx (e.g. unknown model). Counts as an attempt.</summary>
    ClientError,

    /// <summary>Anything else (malformed envelope, missing mock answer). Counts as an attempt.</summary>
    Other,
}

/// <summary>A transport-level failure: the model gave no usable answer at all (as opposed to an invalid answer).</summary>
public sealed class LlmTransportException(LlmErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public LlmErrorKind Kind { get; } = kind;

    /// <summary>When true the job should not lose an attempt and the worker should pause.</summary>
    public bool IsOutage => Kind is LlmErrorKind.Unreachable or LlmErrorKind.Configuration;
}

public interface ILlmClient
{
    string Provider { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);

    Task<LlmHealth> CheckHealthAsync(CancellationToken cancellationToken);
}
