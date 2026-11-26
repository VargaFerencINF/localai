namespace EfMailAgent.Jobs;

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string NeedsReview = "needs_review";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Queued, Processing, Done, NeedsReview, Failed];

    public static bool IsTerminal(string status) => status is Done or NeedsReview or Failed;
}

public static class JobSource
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}

/// <summary>A submitted email, as it is enqueued.</summary>
public sealed record NewJob
{
    public required string MessageId { get; init; }
    public string? Account { get; init; }
    public string? Folder { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Subject { get; init; }
    public string? BodyText { get; init; }
    public string? BodyHtml { get; init; }
    public IReadOnlyList<string> AttachmentNames { get; init; } = [];
    public string Source { get; init; } = JobSource.Auto;
    public required string RuleId { get; init; }
}

/// <summary>A row of the jobs table.</summary>
public sealed record Job
{
    public required string Id { get; init; }
    public required string MessageId { get; init; }
    public string? Account { get; init; }
    public string? Folder { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? Sender { get; init; }
    public string? Recipients { get; init; }
    public string? Subject { get; init; }
    public string? BodyText { get; init; }
    public string? BodyHtml { get; init; }
    public IReadOnlyList<string> AttachmentNames { get; init; } = [];
    public required string Source { get; init; }
    public required string RuleId { get; init; }
    public required string Status { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? LastError { get; init; }
    public string? RawResponse { get; init; }
    public string? RepairResponse { get; init; }
    public string? OutputFile { get; init; }
    public int? RowCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? OriginalSender { get; init; }
    public string? PromptSystem { get; init; }
    public string? PromptUser { get; init; }
    public string? ParsedRows { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>Final result of a job, written in one update.</summary>
public sealed record JobCompletion(
    string Status,
    string? Error,
    string? RawResponse,
    string? RepairResponse,
    string? OutputFile,
    int? RowCount,
    string? ParsedRows,
    long? DurationMs);
