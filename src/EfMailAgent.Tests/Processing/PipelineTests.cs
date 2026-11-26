using System.Text;
using EfMailAgent.Configuration;
using EfMailAgent.Email;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Processing;
using EfMailAgent.Tests.Fakes;

namespace EfMailAgent.Tests.Processing;

/// <summary>Integration tests of the whole pipeline against the fake LLM HTTP server (Ollama API).</summary>
public class PipelineTests : IAsyncLifetime
{
    private FakeLlmServer _server = null!;

    public async Task InitializeAsync() => _server = await FakeLlmServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private PipelineHarness Harness(Action<AppSettings>? configure = null) => new(s =>
    {
        s.Llm.Provider = LlmProviders.Ollama;
        s.Llm.BaseUrl = _server.BaseUrl;
        configure?.Invoke(s);
    });

    [Fact]
    public async Task Valid_answer_gives_done_and_csv()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow);
        using var h = Harness();
        var job = h.Enqueue();

        var (result, done) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.Done, result.Status);
        Assert.Equal(JobStatus.Done, done.Status);
        Assert.Equal(1, done.Attempts);
        Assert.Equal(1, done.RowCount);
        Assert.NotNull(done.FinishedAt);
        Assert.Equal(LlmAnswers.ValidOneRow, done.RawResponse);
        Assert.Null(done.RepairResponse);
        Assert.Contains("<email>", done.PromptUser);
        Assert.Contains("## A kinyerendő mezők", done.PromptSystem);
        var file = Assert.Single(h.CsvFiles());
        Assert.Equal(file, done.OutputFile);
        Assert.Equal("eloreendeles_2026-11.csv", Path.GetFileName(file));
        var lines = File.ReadAllText(file, Encoding.UTF8).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith($"{job.Id};2026-11-26 09:30;2026-11-25 09:41;", lines[1]);
        Assert.Contains(";ok;Horgász Centrum Kft.;;Feeder bot 3,6 m;;10;db;18500;HUF;2027-03-01;;magas", lines[1]);
        Assert.Equal(1, _server.CallCount);
    }

    [Fact]
    public async Task Invalid_then_valid_after_repair_gives_done()
    {
        _server.Handler = (_, index) => new FakeReply(index == 0 ? LlmAnswers.Invalid : LlmAnswers.ValidOneRow);
        using var h = Harness();
        h.Enqueue();

        var (_, job) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.Done, job.Status);
        Assert.Equal(LlmAnswers.Invalid, job.RawResponse);
        Assert.Equal(LlmAnswers.ValidOneRow, job.RepairResponse);
        var repairRequest = _server.ChatRequests.ElementAt(1);
        var messages = repairRequest["messages"]!.AsArray();
        Assert.Equal(4, messages.Count);
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal(LlmAnswers.Invalid, messages[2]!["content"]!.GetValue<string>());
        var repairText = messages[3]!["content"]!.GetValue<string>();
        Assert.Contains("nem felel meg az előírt JSON sémának", repairText);
        Assert.Contains("/rows/0", repairText);
        Assert.Single(h.CsvFiles());
    }

    [Fact]
    public async Task Invalid_twice_gives_failed_with_raw_responses_and_no_csv()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.Invalid);
        using var h = Harness();
        h.Enqueue();

        var (result, job) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(LlmAnswers.Invalid, job.RawResponse);
        Assert.Equal(LlmAnswers.Invalid, job.RepairResponse);
        Assert.Contains("javító hívás után sem felel meg", job.LastError);
        Assert.Equal(2, _server.CallCount);
        Assert.Empty(h.CsvFiles());
    }

    [Fact]
    public async Task Not_json_at_all_goes_through_repair()
    {
        _server.Handler = (_, index) => new FakeReply(index == 0 ? "Íme a válasz: nincs" : LlmAnswers.ValidOneRow);
        using var h = Harness();
        h.Enqueue();

        var (_, job) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.Done, job.Status);
        Assert.Contains("nem érvényes JSON", _server.ChatRequests.ElementAt(1)["messages"]![3]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Zero_rows_gives_needs_review_without_csv()
    {
        _server.Handler = (_, _) => new FakeReply("""{"rows": []}""");
        using var h = Harness();
        h.Enqueue();

        var (_, job) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.NeedsReview, job.Status);
        Assert.Equal(0, job.RowCount);
        Assert.Null(job.OutputFile);
        Assert.Contains("egyetlen tételt sem", job.LastError);
        Assert.Empty(h.CsvFiles());
    }

    [Fact]
    public async Task Low_confidence_or_empty_required_field_gives_needs_review_but_rows_are_written()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.Rows(LlmAnswers.Row(), LlmAnswers.Row("alacsony"), LlmAnswers.Row(partner: " ")));
        using var h = Harness();
        h.Enqueue();

        var (_, job) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.NeedsReview, job.Status);
        Assert.Equal(3, job.RowCount);
        var lines = File.ReadAllLines(job.OutputFile!);
        Assert.Contains(";ok;", lines[1]);
        Assert.Contains(";ellenorzendo;", lines[2]);
        Assert.Contains(";ellenorzendo;", lines[3]);
        Assert.Contains("Alacsony bizonyosság", job.ParsedRows);
        Assert.Contains("partner_nev", job.ParsedRows);
    }

    [Fact]
    public async Task Timeout_schedules_a_retry_with_backoff()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow, Delay: TimeSpan.FromSeconds(5));
        using var h = Harness(s => s.Llm.TimeoutSeconds = 1);
        h.Enqueue();

        var (result, job) = await h.ProcessNextAsync();

        Assert.Equal(AttemptResult.RetryScheduled, result.Result);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(30), job.NextAttemptAt);
        Assert.Contains("időtúllépés", job.LastError);
        Assert.Null(h.Queue.DequeueNext()); // not due yet

        h.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.NotNull(h.Queue.DequeueNext());
    }

    [Fact]
    public async Task Transport_errors_fail_the_job_after_max_attempts()
    {
        _server.Handler = (_, _) => new FakeReply("", StatusCode: 500);
        using var h = Harness();
        h.Enqueue();

        var expectedDelays = new[] { 30, 120, 600 };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (result, job) = await h.ProcessNextAsync();
            Assert.Equal(AttemptResult.RetryScheduled, result.Result);
            Assert.Equal(attempt, job.Attempts);
            Assert.Equal(h.Clock.GetUtcNow().AddSeconds(expectedDelays[attempt - 1]), job.NextAttemptAt);
            h.Clock.Advance(TimeSpan.FromSeconds(expectedDelays[attempt - 1]));
        }

        var (last, failed) = await h.ProcessNextAsync();
        Assert.Equal(JobStatus.Failed, last.Status);
        Assert.Equal(4, failed.Attempts);
        Assert.Contains("HTTP 500", failed.LastError);
        Assert.Contains("4. kísérlet után", failed.LastError);
    }

    [Fact]
    public async Task Unreachable_llm_pauses_without_using_attempts()
    {
        using var h = Harness(s => s.Llm.BaseUrl = "http://127.0.0.1:1");
        h.Enqueue();

        var (result, job) = await h.ProcessNextAsync();

        Assert.Equal(AttemptResult.PausedForOutage, result.Result);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Contains("nem érhető el", job.LastError);
    }

    [Fact]
    public async Task Missing_model_is_an_outage_not_a_failure()
    {
        using var h = Harness(s => s.Llm.Model = "");
        h.Enqueue();

        var (result, job) = await h.ProcessNextAsync();

        Assert.Equal(AttemptResult.PausedForOutage, result.Result);
        Assert.Equal(0, job.Attempts);
        Assert.Contains("Nincs beállítva LLM modell", job.LastError);
    }

    [Fact]
    public async Task Unknown_rule_fails_the_job()
    {
        using var h = Harness();
        h.Enqueue(ruleId: "nincs_ilyen");

        var (_, job) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("nem található", job.LastError);
    }

    [Fact]
    public async Task Per_rule_llm_overrides_are_sent()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow);
        using var h = Harness(s => s.Llm.Temperature = 0.7);
        var yaml = Path.Combine(h.Settings.RulesDir, "eloreendeles", "rule.yaml");
        File.WriteAllText(yaml, File.ReadAllText(yaml).Replace("  model: null", "  model: egyedi-modell"));
        h.Rules.Reload();
        h.Enqueue();

        await h.ProcessNextAsync();

        var request = _server.ChatRequests.Single();
        Assert.Equal("egyedi-modell", request["model"]!.GetValue<string>());
        Assert.Equal(0, request["options"]!["temperature"]!.GetValue<double>()); // rule.yaml says temperature: 0
        Assert.Equal(16384, request["options"]!["num_ctx"]!.GetValue<int>());
    }
}

/// <summary>Pipeline tests with the mock provider and the DEV_MODE demo fixtures.</summary>
public class MockPipelineTests
{
    [Fact]
    public async Task Demo_email_produces_the_expected_csv()
    {
        using var h = new PipelineHarness();
        var email = EmailFile.Load(Path.Combine(TestPaths.SamplesRoot, "emails", "demo_eloreendeles_01.eml"));
        h.Queue.Enqueue(new NewJob
        {
            MessageId = email.MessageId!,
            From = email.From,
            To = email.To,
            Subject = email.Subject,
            ReceivedAt = email.ReceivedAt,
            BodyText = email.BodyText,
            RuleId = "eloreendeles",
        });

        var (result, job) = await h.ProcessNextAsync();

        Assert.Equal(JobStatus.NeedsReview, result.Status);
        Assert.Equal(5, job.RowCount);
        Assert.Equal("Nagy Gábor <nagy.gabor@horgaszcentrum.example>", job.OriginalSender);

        var actual = File.ReadAllBytes(job.OutputFile!);
        var expected = File.ReadAllBytes(Path.Combine(TestPaths.SamplesRoot, "expected", "demo_eloreendeles_01.csv"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, actual[..3]);
        Assert.Equal(MaskVolatileColumns(expected), MaskVolatileColumns(actual));

        var text = Encoding.UTF8.GetString(actual);
        Assert.DoesNotContain("\r\r", text);
        Assert.Equal(text.Split('\n').Length - 1, text.Split("\r\n").Length - 1); // every LF is part of CRLF
        Assert.Contains("\"Kartononként 12 db; ár: a szokásos; szállítás: március eleje\"", text);
        Assert.Contains($"{job.Id};2026-11-26 09:30;", text);

        Assert.True(File.Exists(Path.Combine(h.Settings.DataDir, "mock-prompts", job.Id + ".txt")));
    }

    [Fact]
    public async Task Mock_scenarios_drive_the_error_paths()
    {
        using var h = new PipelineHarness();
        h.Enqueue(subject: "Előrendelés [mock:invalid]");
        h.Enqueue(subject: "Előrendelés [mock:invalid-then-valid]");
        h.Enqueue(subject: "Előrendelés [mock:empty]");

        Assert.Equal(JobStatus.Failed, (await h.ProcessNextAsync()).Job.Status);
        Assert.Equal(JobStatus.NeedsReview, (await h.ProcessNextAsync()).Job.Status); // repaired; demo answer has an "alacsony" row
        Assert.Equal(JobStatus.NeedsReview, (await h.ProcessNextAsync()).Job.Status);
    }

    [Fact]
    public async Task Mock_timeout_scenario_schedules_retry()
    {
        using var h = new PipelineHarness(s => s.Llm.TimeoutSeconds = 1);
        h.Enqueue(subject: "Előrendelés [mock:timeout]");

        var (result, job) = await h.ProcessNextAsync();

        Assert.Equal(AttemptResult.RetryScheduled, result.Result);
        Assert.Equal(JobStatus.Queued, job.Status);
    }

    internal static string MaskVolatileColumns(byte[] csv)
    {
        var lines = Encoding.UTF8.GetString(csv).Split("\r\n");
        return string.Join("\r\n", lines.Select((line, i) =>
        {
            if (i == 0 || line.Length == 0)
                return line;
            var parts = line.Split(';', 3);
            return $"<job_id>;<feldolgozva>;{parts[2]}";
        }));
    }
}
