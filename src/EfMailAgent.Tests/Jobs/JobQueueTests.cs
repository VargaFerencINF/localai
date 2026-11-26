using EfMailAgent.Jobs;
using EfMailAgent.Tests.Fakes;

namespace EfMailAgent.Tests.Jobs;

public class JobQueueTests
{
    [Fact]
    public void Database_is_migrated_to_latest_version()
    {
        using var h = new PipelineHarness();
        Assert.Equal(JobDatabase.LatestVersion, h.Database.CurrentVersion());
        Assert.True(File.Exists(Path.Combine(h.Settings.DataDir, "queue.db")));

        // Opening again must not re-run migrations.
        var again = new JobDatabase(h.Settings.DataDir);
        Assert.Equal(JobDatabase.LatestVersion, again.CurrentVersion());
    }

    [Theory]
    [InlineData("<abc@x.example>", "abc@x.example")]
    [InlineData("abc@x.example", " <abc@x.example> ")]
    public void Duplicate_message_id_returns_existing_job(string first, string second)
    {
        using var h = new PipelineHarness();
        var (job, created) = h.Queue.Enqueue(New(first));
        var (duplicate, createdAgain) = h.Queue.Enqueue(New(second));

        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(job.Id, duplicate.Id);
        Assert.Equal("<abc@x.example>", duplicate.MessageId);
        Assert.Single(h.Queue.List());
    }

    [Fact]
    public void Dequeue_is_fifo_and_marks_processing()
    {
        using var h = new PipelineHarness();
        var a = h.Enqueue();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var b = h.Enqueue();

        var first = h.Queue.DequeueNext()!;
        var second = h.Queue.DequeueNext()!;

        Assert.Equal(a.Id, first.Id);
        Assert.Equal(b.Id, second.Id);
        Assert.Equal(JobStatus.Processing, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.NotNull(first.StartedAt);
        Assert.Null(h.Queue.DequeueNext());
        Assert.Equal(2, h.Queue.QueueLength());
    }

    [Fact]
    public void Retry_time_is_respected_and_later_jobs_are_not_blocked()
    {
        using var h = new PipelineHarness();
        var a = h.Enqueue();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var b = h.Enqueue();

        h.Queue.DequeueNext();
        h.Queue.ScheduleRetry(a.Id, "hiba", h.Clock.GetUtcNow().AddMinutes(2));

        Assert.Equal(b.Id, h.Queue.DequeueNext()!.Id);
        Assert.Null(h.Queue.DequeueNext());
        Assert.Equal(h.Clock.GetUtcNow().AddMinutes(2), h.Queue.NextScheduledAttempt());

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var retried = h.Queue.DequeueNext()!;
        Assert.Equal(a.Id, retried.Id);
        Assert.Equal(2, retried.Attempts);
    }

    [Fact]
    public void Release_does_not_use_up_an_attempt()
    {
        using var h = new PipelineHarness();
        var job = h.Enqueue();
        h.Queue.DequeueNext();

        h.Queue.Release(job.Id, "leállítás", null);

        var after = h.Queue.Get(job.Id)!;
        Assert.Equal(JobStatus.Queued, after.Status);
        Assert.Equal(0, after.Attempts);
    }

    [Fact]
    public void Requeue_only_for_failed_or_needs_review()
    {
        using var h = new PipelineHarness();
        var job = h.Enqueue();
        Assert.False(h.Queue.Requeue(job.Id));

        h.Queue.DequeueNext();
        h.Queue.Complete(job.Id, new JobCompletion(JobStatus.Failed, "hiba", null, null, null, null, null, null));
        Assert.True(h.Queue.Requeue(job.Id));

        var after = h.Queue.Get(job.Id)!;
        Assert.Equal(JobStatus.Queued, after.Status);
        Assert.Equal(0, after.Attempts);
        Assert.Null(after.LastError);
    }

    [Fact]
    public void Fields_round_trip()
    {
        using var h = new PipelineHarness();
        var (job, _) = h.Queue.Enqueue(New("<rt@x>") with
        {
            Account = "eloreendeles@x",
            Folder = "Inbox",
            AttachmentNames = ["rendeles.xlsx", "árlista.pdf"],
            BodyHtml = "<p>szia</p>",
            Source = JobSource.Manual,
        });

        var loaded = h.Queue.Get(job.Id)!;
        Assert.Equal("eloreendeles@x", loaded.Account);
        Assert.Equal("Inbox", loaded.Folder);
        Assert.Equal(["rendeles.xlsx", "árlista.pdf"], loaded.AttachmentNames);
        Assert.Equal("<p>szia</p>", loaded.BodyHtml);
        Assert.Equal(JobSource.Manual, loaded.Source);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 41, 0, TimeSpan.Zero), loaded.ReceivedAt);
        Assert.Equal(h.Clock.GetUtcNow(), loaded.CreatedAt);
        Assert.Same(loaded.MessageId, loaded.MessageId);
        Assert.Equal(loaded.Id, h.Queue.GetByMessageId("rt@x")!.Id);
        Assert.Single(h.Queue.List(JobStatus.Queued));
        Assert.Empty(h.Queue.List(JobStatus.Done));
    }

    private static NewJob New(string messageId) => new()
    {
        MessageId = messageId,
        Subject = "Előrendelés",
        BodyText = "x",
        RuleId = "eloreendeles",
        ReceivedAt = new DateTimeOffset(2026, 11, 25, 9, 41, 0, TimeSpan.FromHours(1)),
    };
}
