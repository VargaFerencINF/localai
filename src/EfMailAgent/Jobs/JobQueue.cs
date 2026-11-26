using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EfMailAgent.Jobs;

/// <summary>Persistent FIFO job queue on top of <see cref="JobDatabase"/>. Thread-safe; every call uses its own connection.</summary>
public sealed class JobQueue(JobDatabase database, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Raised after a new job was inserted or a job became queued again (wakes the worker).</summary>
    public event EventHandler? JobAvailable;

    public JobDatabase Database => database;

    /// <summary>Inserts the job unless its Message-ID is already known; then the existing job is returned.</summary>
    public (Job Job, bool Created) Enqueue(NewJob newJob)
    {
        var messageId = MessageIds.Canonicalize(newJob.MessageId);
        if (messageId.Length == 0)
            throw new ArgumentException("A Message-ID nem lehet üres.", nameof(newJob));

        var id = Guid.NewGuid().ToString();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO jobs (id, message_id, account, folder, received_at, sender, recipients, subject, body_text,
                                  body_html, attachment_names, source, rule_id, status, attempts, created_at)
                VALUES ($id, $message_id, $account, $folder, $received_at, $sender, $recipients, $subject, $body_text,
                        $body_html, $attachment_names, $source, $rule_id, 'queued', 0, $created_at)
                ON CONFLICT (message_id) DO NOTHING
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$message_id", messageId);
            command.Parameters.AddWithValue("$account", Db(newJob.Account));
            command.Parameters.AddWithValue("$folder", Db(newJob.Folder));
            command.Parameters.AddWithValue("$received_at", Db(newJob.ReceivedAt?.ToString("O", CultureInfo.InvariantCulture)));
            command.Parameters.AddWithValue("$sender", Db(newJob.From));
            command.Parameters.AddWithValue("$recipients", Db(newJob.To));
            command.Parameters.AddWithValue("$subject", Db(newJob.Subject));
            command.Parameters.AddWithValue("$body_text", Db(newJob.BodyText));
            command.Parameters.AddWithValue("$body_html", Db(newJob.BodyHtml));
            command.Parameters.AddWithValue("$attachment_names", JsonSerializer.Serialize(newJob.AttachmentNames));
            command.Parameters.AddWithValue("$source", newJob.Source);
            command.Parameters.AddWithValue("$rule_id", newJob.RuleId);
            command.Parameters.AddWithValue("$created_at", Now());
            var inserted = command.ExecuteNonQuery() == 1;

            if (!inserted)
                return (GetByMessageId(messageId)!, false);
        }

        JobAvailable?.Invoke(this, EventArgs.Empty);
        return (Get(id)!, true);
    }

    public Job? Get(string id) => QuerySingle("SELECT * FROM jobs WHERE id = $p", id);

    public Job? GetByMessageId(string messageId) =>
        QuerySingle("SELECT * FROM jobs WHERE message_id = $p", MessageIds.Canonicalize(messageId));

    public IReadOnlyList<Job> List(string? status = null, int limit = 50)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = status is null
            ? "SELECT * FROM jobs ORDER BY created_at DESC, rowid DESC LIMIT $limit"
            : "SELECT * FROM jobs WHERE status = $status ORDER BY created_at DESC, rowid DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        if (status is not null)
            command.Parameters.AddWithValue("$status", status);
        return ReadAll(command);
    }

    /// <summary>Jobs waiting or running.</summary>
    public int QueueLength()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM jobs WHERE status IN ('queued', 'processing')";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Atomically takes the oldest queued job whose retry time has come, marks it processing and counts the attempt.
    /// </summary>
    public Job? DequeueNext() => DequeueWhere("status = 'queued' AND (next_attempt_at IS NULL OR next_attempt_at <= $now) ORDER BY created_at, rowid LIMIT 1", null);

    /// <summary>Like <see cref="DequeueNext"/> but for one specific job (CLI), ignoring its retry time.</summary>
    public Job? TryDequeue(string id) => DequeueWhere("id = $id AND status = 'queued'", id);

    /// <summary>Earliest retry time among queued jobs that are not due yet (to schedule the next wake-up).</summary>
    public DateTimeOffset? NextScheduledAttempt()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(next_attempt_at) FROM jobs WHERE status = 'queued' AND next_attempt_at > $now";
        command.Parameters.AddWithValue("$now", Now());
        return ParseDate(command.ExecuteScalar() as string);
    }

    /// <summary>Startup recovery: jobs left in processing by a crash or kill go back to queued (attempt not counted).</summary>
    public int RecoverInterrupted()
    {
        var count = Execute("""
            UPDATE jobs SET status = 'queued', attempts = MAX(attempts - 1, 0), started_at = NULL, next_attempt_at = NULL,
                            last_error = 'A feldolgozás megszakadt (újraindítás), a feladat visszakerült a sorba.'
            WHERE status = 'processing'
            """);
        if (count > 0)
            JobAvailable?.Invoke(this, EventArgs.Empty);
        return count;
    }

    /// <summary>Transport error with attempts left: back to the queue after the backoff delay.</summary>
    public void ScheduleRetry(string id, string error, DateTimeOffset nextAttemptAt) =>
        Execute("UPDATE jobs SET status = 'queued', last_error = $error, next_attempt_at = $next WHERE id = $id",
            ("$id", id), ("$error", error), ("$next", nextAttemptAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

    /// <summary>Back to the queue without using up an attempt (LLM unreachable, shutdown).</summary>
    public void Release(string id, string reason, DateTimeOffset? nextAttemptAt)
    {
        Execute("""
            UPDATE jobs SET status = 'queued', attempts = MAX(attempts - 1, 0), last_error = $error, next_attempt_at = $next
            WHERE id = $id AND status = 'processing'
            """,
            ("$id", id), ("$error", reason), ("$next", nextAttemptAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
    }

    public void SavePrompt(string id, string system, string user, string? originalSender) =>
        Execute("UPDATE jobs SET prompt_system = $s, prompt_user = $u, original_sender = $o WHERE id = $id",
            ("$id", id), ("$s", system), ("$u", user), ("$o", originalSender));

    public void Complete(string id, JobCompletion completion) =>
        Execute("""
            UPDATE jobs SET status = $status, last_error = $error, raw_response = $raw, repair_response = $repair,
                            output_file = $output, row_count = $rows, parsed_rows = $parsed, duration_ms = $duration,
                            finished_at = $now, next_attempt_at = NULL
            WHERE id = $id
            """,
            ("$id", id), ("$status", completion.Status), ("$error", completion.Error), ("$raw", completion.RawResponse),
            ("$repair", completion.RepairResponse), ("$output", completion.OutputFile), ("$rows", completion.RowCount),
            ("$parsed", completion.ParsedRows), ("$duration", completion.DurationMs));

    /// <summary>Manual re-run of a failed / needs_review job (API retry endpoint). Returns false for other states.</summary>
    public bool Requeue(string id)
    {
        var changed = Execute("""
            UPDATE jobs SET status = 'queued', attempts = 0, next_attempt_at = NULL, last_error = NULL, started_at = NULL,
                            finished_at = NULL
            WHERE id = $id AND status IN ('failed', 'needs_review')
            """, ("$id", id)) == 1;
        if (changed)
            JobAvailable?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    private Job? DequeueWhere(string where, string? id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE jobs SET status = 'processing', attempts = attempts + 1, started_at = $now
            WHERE id = (SELECT id FROM jobs WHERE {where})
            RETURNING *
            """;
        command.Parameters.AddWithValue("$now", Now());
        if (id is not null)
            command.Parameters.AddWithValue("$id", id);
        return ReadAll(command).FirstOrDefault();
    }

    private Job? QuerySingle(string sql, string parameter)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$p", parameter);
        return ReadAll(command).FirstOrDefault();
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$now", Now());
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private string Now() => _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static List<Job> ReadAll(SqliteCommand command)
    {
        var result = new List<Job>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(Map(reader));
        return result;
    }

    private static Job Map(SqliteDataReader r)
    {
        string? S(string name)
        {
            var ordinal = r.GetOrdinal(name);
            return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
        }

        long? L(string name)
        {
            var ordinal = r.GetOrdinal(name);
            return r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal);
        }

        return new Job
        {
            Id = S("id")!,
            MessageId = S("message_id")!,
            Account = S("account"),
            Folder = S("folder"),
            ReceivedAt = ParseDate(S("received_at")),
            Sender = S("sender"),
            Recipients = S("recipients"),
            Subject = S("subject"),
            BodyText = S("body_text"),
            BodyHtml = S("body_html"),
            AttachmentNames = S("attachment_names") is { } names
                ? JsonSerializer.Deserialize<List<string>>(names) ?? []
                : [],
            Source = S("source")!,
            RuleId = S("rule_id")!,
            Status = S("status")!,
            Attempts = (int)(L("attempts") ?? 0),
            NextAttemptAt = ParseDate(S("next_attempt_at")),
            LastError = S("last_error"),
            RawResponse = S("raw_response"),
            RepairResponse = S("repair_response"),
            OutputFile = S("output_file"),
            RowCount = (int?)L("row_count"),
            CreatedAt = ParseDate(S("created_at")) ?? DateTimeOffset.MinValue,
            StartedAt = ParseDate(S("started_at")),
            FinishedAt = ParseDate(S("finished_at")),
            OriginalSender = S("original_sender"),
            PromptSystem = S("prompt_system"),
            PromptUser = S("prompt_user"),
            ParsedRows = S("parsed_rows"),
            DurationMs = L("duration_ms"),
        };
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
