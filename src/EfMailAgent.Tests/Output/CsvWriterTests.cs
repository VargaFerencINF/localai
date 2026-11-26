using System.Text;
using System.Text.Json.Nodes;
using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using EfMailAgent.Output;
using EfMailAgent.Processing;
using EfMailAgent.Rules;

namespace EfMailAgent.Tests.Output;

public class CsvWriterTests : IDisposable
{
    private static readonly TimeZoneInfo Budapest = TimeZoneInfo.FindSystemTimeZoneById("Europe/Budapest");
    private static readonly DateTimeOffset ProcessedAt = new(2026, 3, 5, 7, 15, 0, TimeSpan.Zero);
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Writes_bom_header_crlf_quoting_and_decimal_separator()
    {
        var writer = Writer();
        var rule = TestRule();
        var rows = new[]
        {
            Row(rule, """{"nev": "Alfa; Béta \"idézet\"", "menny": 2.50, "egesz": 3, "kell": true, "datum": "2027-01-31", "tipus": "a"}"""),
            Row(rule, """{"nev": "Több\nsoros", "menny": 1000000.125, "egesz": null, "kell": false, "datum": null, "tipus": null}"""),
        };

        var result = await writer.WriteAsync(rule, Job(), rows, ProcessedAt, "Eredeti <e@x>", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.CreatedFile);
        Assert.Equal("teszt_2026-03-05.csv", Path.GetFileName(result.FilePath));
        var bytes = File.ReadAllBytes(result.FilePath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal(
            "job_id;feldolgozva;email_datum;kuldo;eredeti_kuldo;targy;message_id;statusz;nev;menny;egesz;kell;datum;tipus\r\n"
            + "j1;2026-03-05 08:15;2026-03-04 18:00;Küldő <k@x>;Eredeti <e@x>;Fwd: teszt;<m@x>;ok;\"Alfa; Béta \"\"idézet\"\"\";2,5;3;igen;2027-01-31;a\r\n"
            + "j1;2026-03-05 08:15;2026-03-04 18:00;Küldő <k@x>;Eredeti <e@x>;Fwd: teszt;<m@x>;ok;\"Több\nsoros\";1000000,125;;nem;;\r\n",
            text);
    }

    [Fact]
    public async Task Appends_without_repeating_the_header()
    {
        var writer = Writer();
        var rule = TestRule();
        await writer.WriteAsync(rule, Job(), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);
        var second = await writer.WriteAsync(rule, Job("j2"), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);

        Assert.False(second!.CreatedFile);
        var lines = File.ReadAllLines(second.FilePath);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("j2;", lines[2]);
    }

    [Fact]
    public async Task Changed_columns_start_a_new_versioned_file()
    {
        var writer = Writer();
        var rule = TestRule();
        var first = await writer.WriteAsync(rule, Job(), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);
        var original = File.ReadAllBytes(first!.FilePath);

        var changed = TestRule(extraColumn: true);
        var second = await writer.WriteAsync(changed, Job("j2"), [Row(changed, Simple)], ProcessedAt, null, CancellationToken.None);
        var third = await writer.WriteAsync(changed, Job("j3"), [Row(changed, Simple)], ProcessedAt, null, CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(first.FilePath));
        Assert.Equal("teszt_2026-03-05_v2.csv", Path.GetFileName(second!.FilePath));
        Assert.Equal(second.FilePath, third!.FilePath);
        Assert.EndsWith(";uj", File.ReadAllLines(second.FilePath)[0]);
        Assert.Equal(3, File.ReadAllLines(second.FilePath).Length);
    }

    [Fact]
    public async Task Locked_file_falls_back_to_pending_file()
    {
        var writer = Writer();
        var rule = TestRule();
        var path = (await writer.WriteAsync(rule, Job(), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None))!.FilePath;
        var before = File.ReadAllBytes(path);

        CsvWriteResult? result;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // e.g. Excel
            result = await writer.WriteAsync(rule, Job("j2"), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);

        Assert.True(result!.IsPending);
        Assert.Equal("teszt_2026-03-05_pending_j2.csv", Path.GetFileName(result.FilePath));
        Assert.Equal(before, File.ReadAllBytes(path));
        var pending = File.ReadAllLines(result.FilePath);
        Assert.Equal(2, pending.Length);
        Assert.StartsWith("j2;", pending[1]);
    }

    [Fact]
    public async Task Lock_released_during_retry_writes_to_the_main_file()
    {
        var writer = Writer(lockRetry: TimeSpan.FromSeconds(10));
        var rule = TestRule();
        var path = (await writer.WriteAsync(rule, Job(), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None))!.FilePath;

        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var write = writer.WriteAsync(rule, Job("j2"), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(write.IsCompleted);
        await locker.DisposeAsync();

        var result = await write;
        Assert.False(result!.IsPending);
        Assert.Equal(path, result.FilePath);
        Assert.Equal(3, File.ReadAllLines(path).Length);
    }

    [Fact]
    public async Task Per_job_mode_never_appends()
    {
        var writer = Writer();
        var rule = TestRule(mode: OutputMode.PerJob, pattern: "{ruleId}_{jobId}.csv");
        var a = await writer.WriteAsync(rule, Job("j1"), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);
        var b = await writer.WriteAsync(rule, Job("j1"), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);

        Assert.Equal("teszt_j1.csv", Path.GetFileName(a!.FilePath));
        Assert.Equal("teszt_j1_2.csv", Path.GetFileName(b!.FilePath));
        Assert.Equal(2, File.ReadAllLines(a.FilePath).Length);
    }

    [Fact]
    public async Task Existing_file_without_final_newline_is_continued_on_a_new_line()
    {
        var writer = Writer();
        var rule = TestRule();
        var path = (await writer.WriteAsync(rule, Job(), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None))!.FilePath;
        var content = File.ReadAllBytes(path);
        File.WriteAllBytes(path, content[..^2]); // someone saved it without the last CRLF

        await writer.WriteAsync(rule, Job("j2"), [Row(rule, Simple)], ProcessedAt, null, CancellationToken.None);

        var lines = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("j2;", lines[2]);
    }

    [Fact]
    public async Task Utf8_without_bom_and_dot_decimal_separator_are_configurable()
    {
        var writer = Writer(new CsvSettings { Encoding = CsvEncodings.Utf8, DecimalSeparator = ".", Delimiter = "," });
        var rule = TestRule();
        var path = (await writer.WriteAsync(rule, Job(), [Row(rule, """{"nev": "a,b", "menny": 2.5, "egesz": 1, "kell": null, "datum": null, "tipus": null}""")],
            ProcessedAt, null, CancellationToken.None))!.FilePath;

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.EndsWith(",ok,\"a,b\",2.5,1,,,\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task No_rows_writes_nothing()
    {
        var writer = Writer();
        Assert.Null(await writer.WriteAsync(TestRule(), Job(), [], ProcessedAt, null, CancellationToken.None));
        Assert.False(Directory.Exists(_tmp.Combine("out")) && Directory.EnumerateFiles(_tmp.Combine("out")).Any());
    }

    private const string Simple = """{"nev": "x", "menny": 1, "egesz": 1, "kell": true, "datum": null, "tipus": "a"}""";

    private CsvWriter Writer(CsvSettings? settings = null, TimeSpan? lockRetry = null) =>
        new(settings ?? new CsvSettings(), _tmp.Combine("out"), Budapest, lockRetry: lockRetry ?? TimeSpan.FromMilliseconds(300),
            lockRetryInterval: TimeSpan.FromMilliseconds(50));

    private static Job Job(string id = "j1") => new()
    {
        Id = id,
        MessageId = "<m@x>",
        Source = JobSource.Auto,
        RuleId = "teszt",
        Status = JobStatus.Processing,
        Sender = "Küldő <k@x>",
        Subject = "Fwd: teszt",
        ReceivedAt = new DateTimeOffset(2026, 3, 4, 17, 0, 0, TimeSpan.Zero),
    };

    private static ExtractedRow Row(Rule rule, string json) =>
        ResponseValidator.ClassifyRow(rule.Output, JsonNode.Parse(json)!.AsObject());

    internal static Rule TestRule(bool extraColumn = false, OutputMode mode = OutputMode.Append, string pattern = "{ruleId}_{yyyy}-{MM}-{dd}.csv")
    {
        List<RuleColumn> columns =
        [
            new("nev", ColumnType.String, true, "", []),
            new("menny", ColumnType.Number, false, "", []),
            new("egesz", ColumnType.Integer, false, "", []),
            new("kell", ColumnType.Boolean, false, "", []),
            new("datum", ColumnType.Date, false, "", []),
            new("tipus", ColumnType.Enum, false, "", ["a", "b"]),
        ];
        if (extraColumn)
            columns.Add(new RuleColumn("uj", ColumnType.String, false, "", []));
        return new Rule("teszt", "Teszt", true, 10, ["teszt"], null, "/tmp", "prompt.md", "p", [],
            new RuleOutput(mode, pattern, "rows", columns), new RuleLlmOverrides(null, null, 20000))
        {
            Matcher = new EfMailAgent.Matching.SubjectMatcher("teszt", 10, true, ["teszt"], null),
        };
    }
}
