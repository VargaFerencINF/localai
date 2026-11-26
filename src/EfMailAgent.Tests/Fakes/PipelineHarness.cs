using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Output;
using EfMailAgent.Processing;
using EfMailAgent.Rules;

namespace EfMailAgent.Tests.Fakes;

/// <summary>A complete pipeline (rules, queue, LLM client, CSV writer, processor) in a temp data folder.</summary>
public sealed class PipelineHarness : IDisposable
{
    public PipelineHarness(Action<AppSettings>? configure = null, Func<EffectiveSettings, ILlmClient>? llmFactory = null)
    {
        Temp = new TempDir();
        var appSettings = new AppSettings();
        appSettings.Server.Token = TokenGenerator.NewToken();
        appSettings.Llm.Provider = LlmProviders.Mock;
        appSettings.Llm.MockDir = Path.Combine(TestPaths.SamplesRoot, "mock-responses");
        appSettings.Llm.MockDelayMs = 0;
        appSettings.Llm.Model = "qwen3.5:9b";
        appSettings.Worker.LlmOutagePauseSeconds = 1;
        appSettings.Worker.IdlePollMilliseconds = 100;
        configure?.Invoke(appSettings);

        Settings = new EffectiveSettings(new DataDirectory(Temp.Path, DataDirectorySource.CommandLine), appSettings);
        SettingsBootstrapper.CopyDirectory(TestPaths.SampleRules, Settings.RulesDir);

        Clock = new TestClock();
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(appSettings.App.TimeZone);
        Rules = new RuleRepository(Settings.RulesDir);
        Rules.Reload(force: true);
        Database = new JobDatabase(Settings.DataDir);
        Queue = new JobQueue(Database, Clock);
        Llm = llmFactory?.Invoke(Settings) ?? LlmClientFactory.Create(Settings);
        Csv = new CsvWriter(appSettings.Csv, Settings.OutputDir, TimeZone, lockRetry: TimeSpan.FromMilliseconds(300),
            lockRetryInterval: TimeSpan.FromMilliseconds(50));
        Processor = new JobProcessor(Rules, Queue, Llm, Csv, Settings, TimeZone, Clock);
    }

    public TempDir Temp { get; }
    public EffectiveSettings Settings { get; }
    public TestClock Clock { get; }
    public TimeZoneInfo TimeZone { get; }
    public RuleRepository Rules { get; }
    public JobDatabase Database { get; }
    public JobQueue Queue { get; }
    public ILlmClient Llm { get; }
    public CsvWriter Csv { get; }
    public JobProcessor Processor { get; }

    public Job Enqueue(string subject = "Fwd: Előrendelés teszt", string body = "Kérünk 10 db feeder botot.", string? messageId = null,
        string ruleId = "eloreendeles")
    {
        var (job, created) = Queue.Enqueue(new NewJob
        {
            MessageId = messageId ?? MessageIds.Generate("test"),
            From = "Kovács Péter <kovacs.peter@ertekesites.example>",
            To = "eloreendeles@ertekesites.example",
            Subject = subject,
            BodyText = body,
            ReceivedAt = new DateTimeOffset(2026, 11, 25, 9, 41, 0, TimeSpan.FromHours(1)),
            RuleId = ruleId,
        });
        Assert.True(created);
        return job;
    }

    public async Task<(ProcessResult Result, Job Job)> ProcessNextAsync()
    {
        var job = Queue.DequeueNext();
        Assert.NotNull(job);
        var result = await Processor.ProcessAsync(job, CancellationToken.None);
        return (result, Queue.Get(job.Id)!);
    }

    public string[] CsvFiles() => Directory.Exists(Settings.OutputDir) ? Directory.GetFiles(Settings.OutputDir) : [];

    public void Dispose()
    {
        Rules.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Temp.Dispose();
    }
}

public static class LlmAnswers
{
    public static string Row(string confidence = "magas", string partner = "Horgász Centrum Kft.", object? quantity = null) =>
        $$"""
        {"partner_nev": "{{partner}}", "partner_kod": null, "cikk_megnevezes": "Feeder bot 3,6 m", "cikkszam": null,
         "mennyiseg": {{quantity ?? 10}}, "mennyisegi_egyseg": "db", "egysegar": 18500, "penznem": "HUF",
         "kert_szallitasi_datum": "2027-03-01", "megjegyzes": null, "bizonyossag": "{{confidence}}"}
        """;

    public static string Rows(params string[] rows) => $$"""{"rows": [{{string.Join(",", rows)}}]}""";

    public static string ValidOneRow => Rows(Row());

    public static string Invalid => """{"rows": [{"partner_nev": "X", "mennyiseg": "tíz", "bizonyossag": "biztos"}]}""";
}
