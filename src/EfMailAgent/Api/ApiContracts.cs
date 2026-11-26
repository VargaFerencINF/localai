using System.Text.Json;
using EfMailAgent.Jobs;

namespace EfMailAgent.Api;

// Public HTTP contract (CLAUDE.md section 7). JSON is camelCase; extra fields beyond the spec are additive only.

public sealed record HealthResponse(
    string Status,
    string Version,
    bool LlmReachable,
    int QueueLength,
    string RulesVersion,
    string LlmProvider,
    string? LlmModel,
    string? LlmError,
    bool LlmOutage,
    int ValidRules,
    int InvalidRules);

public sealed record MatcherDto(string RuleId, string Name, IReadOnlyList<string> SubjectContains, string? SubjectRegex, int Priority);

public sealed record MatchersResponse(string RulesVersion, IReadOnlyList<MatcherDto> Matchers);

/// <summary>Body of POST /api/jobs.</summary>
public sealed class SubmitJobRequest
{
    public string? MessageId { get; set; }
    public string? Account { get; set; }
    public string? Folder { get; set; }

    /// <summary>ISO 8601 date-time, e.g. 2026-11-25T08:41:00.000Z.</summary>
    public string? ReceivedAt { get; set; }

    public string? From { get; set; }

    /// <summary>A string, or an array of recipient strings (joined with ", ").</summary>
    public JsonElement? To { get; set; }

    public string? Subject { get; set; }
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }
    public List<string>? AttachmentNames { get; set; }

    /// <summary>"auto" (default) or "manual".</summary>
    public string? Source { get; set; }

    /// <summary>Explicit rule (manual submission from the context menu); skips subject matching.</summary>
    public string? RuleId { get; set; }
}

public sealed record SubmitJobResponse(string JobId, string RuleId, string Status, bool Duplicate);

public sealed record JobDto(
    string Id,
    string Status,
    string RuleId,
    int Attempts,
    string? Error,
    string? OutputFile,
    int? RowCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    string MessageId,
    string? Subject,
    string Source,
    DateTimeOffset? NextAttemptAt)
{
    public static JobDto From(Job job) => new(job.Id, job.Status, job.RuleId, job.Attempts, job.LastError, job.OutputFile,
        job.RowCount, job.CreatedAt, job.FinishedAt, job.MessageId, job.Subject, job.Source, job.NextAttemptAt);
}

public sealed record JobListResponse(IReadOnlyList<JobDto> Jobs);

/// <summary>Error body: a Hungarian message plus optional per-field messages.</summary>
public sealed record ErrorResponse(
    string Error,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Errors = null);
