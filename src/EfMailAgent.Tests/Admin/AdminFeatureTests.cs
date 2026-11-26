using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Jobs;
using EfMailAgent.Tests.Api;
using EfMailAgent.Tests.Processing;

namespace EfMailAgent.Tests.Admin;

public class AdminFeatureTests : IAsyncLifetime
{
    private ApiFixture _api = null!;
    private AdminTestClient _admin = null!;

    public async Task InitializeAsync()
    {
        _api = await ApiFixture.StartAsync();
        _admin = new AdminTestClient(_api);
        await _admin.LoginAsync();
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Overview_reports_llm_queue_rules_and_addon()
    {
        var overview = await _admin.GetJsonAsync("/admin/api/overview");

        Assert.True(overview["llm"]!["reachable"]!.GetValue<bool>());
        Assert.Equal("mock", overview["llm"]!["provider"]!.GetValue<string>());
        Assert.Equal(0, overview["queueLength"]!.GetValue<int>());
        Assert.Equal(1, overview["rules"]!["enabled"]!.GetValue<int>());
        Assert.Equal(0, overview["counts"]!["needs_review"]!.GetValue<int>());
        Assert.False(overview["addon"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Test_console_runs_the_rule_without_touching_queue_or_csv()
    {
        var demo = EfMailAgent.Email.EmailFile.Load(Path.Combine(TestPaths.SamplesRoot, "emails", "demo_eloreendeles_01.eml"));

        var (status, result) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/test",
            new { ruleId = "eloreendeles", subject = demo.Subject, from = demo.From, body = demo.BodyText });

        Assert.Equal(200, status);
        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.True(result["valid"]!.GetValue<bool>());
        Assert.Equal("needs_review", result["status"]!.GetValue<string>());
        Assert.Equal(5, result["rows"]!.AsArray().Count);
        Assert.Equal("ellenorzendo", result["rows"]![4]!["status"]!.GetValue<string>());
        Assert.Equal("Horgász Centrum Kft.", result["rows"]![0]!["values"]!["partner_nev"]!.GetValue<string>());
        Assert.Equal(11, result["columns"]!.AsArray().Count);
        Assert.Contains("<email>", result["prompt"]!["user"]!.GetValue<string>());
        Assert.Equal("Nagy Gábor <nagy.gabor@horgaszcentrum.example>", result["originalSender"]!.GetValue<string>());
        Assert.Empty(_api.Queue.List());
        Assert.False(Directory.Exists(_api.Settings.OutputDir) && Directory.GetFiles(_api.Settings.OutputDir).Length > 0);

        var (repairedStatus, repaired) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/test",
            new { ruleId = "eloreendeles", subject = "Előrendelés [mock:invalid-then-valid]", from = "x", body = "y" });
        Assert.Equal(200, repairedStatus);
        Assert.NotNull(repaired!["repairResponse"]!.GetValue<string>());
        Assert.NotEmpty(repaired["firstErrors"]!.AsArray());

        var (missing, _) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/test", new { ruleId = "nincs", subject = "", from = "", body = "" });
        Assert.Equal(404, missing);
    }

    [Fact]
    public async Task Sample_emails_are_offered_with_metadata()
    {
        var samples = (await _admin.GetJsonAsync("/admin/api/samples")).AsArray();
        var table = samples.Single(s => s!["file"]!.GetValue<string>() == "elorendeles_03_tablazat.txt")!;
        Assert.Equal("TOV: Előrendelési lista – Csalihalász Kft.", table["subject"]!.GetValue<string>());
        Assert.Contains("Csalihalász", table["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_show_a_masked_token_and_apply_llm_and_csv_changes_live()
    {
        var settings = await _admin.GetJsonAsync("/admin/api/settings");
        var token = _api.Settings.Settings.Server.Token;
        Assert.DoesNotContain(token, settings.ToJsonString());
        Assert.Contains("…", settings["tokenPreview"]!.GetValue<string>());

        var update = settings.AsObject();
        update["llm"]!["model"] = "qwen3.5:9b";
        update["llm"]!["temperature"] = 0.3;
        update["csv"]!["decimalSeparator"] = ".";
        update["outputFolder"] = "kimenet";
        var (status, result) = await _admin.JsonAsync(HttpMethod.Put, "/admin/api/settings", update);

        Assert.Equal(200, status);
        Assert.Empty(result!["restartRequired"]!.AsArray());
        Assert.Contains(result["appliedNow"]!.AsArray(), a => a!.GetValue<string>().Contains("AI"));
        Assert.Equal("qwen3.5:9b", _api.Settings.Settings.Llm.Model);
        Assert.Equal(".", _api.Settings.Settings.Csv.DecimalSeparator);
        Assert.EndsWith("kimenet", _api.Settings.OutputDir);
        var file = JsonNode.Parse(File.ReadAllText(_api.Settings.SettingsFile))!;
        Assert.Equal("qwen3.5:9b", file["Llm"]!["Model"]!.GetValue<string>());
        Assert.Equal(token, file["Server"]!["Token"]!.GetValue<string>());
        Assert.True(File.Exists(_api.Settings.SettingsFile + ".bak"));

        // The next job writes into the new output folder.
        var submitted = await (await _api.SubmitAsync(ApiFixture.Payload(messageId: "<live@x>"))).Content.ReadFromJsonAsync<JsonNode>();
        var id = submitted!["jobId"]!.GetValue<string>();
        await JobWorkerTests.WaitUntil(() => JobStatus.IsTerminal(_api.Queue.Get(id)!.Status), TimeSpan.FromSeconds(10));
        Assert.StartsWith(Path.Combine(_api.Settings.DataDirectory.Path, "kimenet"), _api.Queue.Get(id)!.OutputFile);
    }

    [Fact]
    public async Task Invalid_settings_are_refused_and_port_change_needs_restart()
    {
        var settings = (await _admin.GetJsonAsync("/admin/api/settings")).AsObject();
        settings["csv"]!["decimalSeparator"] = ";";
        var (status, result) = await _admin.JsonAsync(HttpMethod.Put, "/admin/api/settings", settings);
        Assert.Equal(422, status);
        Assert.Contains(result!["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("DecimalSeparator"));
        Assert.Equal(",", _api.Settings.Settings.Csv.DecimalSeparator);

        settings["csv"]!["decimalSeparator"] = ",";
        var oldPort = _api.Settings.Settings.Server.Port;
        settings["port"] = 48000;
        var (portStatus, portResult) = await _admin.JsonAsync(HttpMethod.Put, "/admin/api/settings", settings);
        Assert.Equal(200, portStatus);
        Assert.Single(portResult!["restartRequired"]!.AsArray());
        Assert.Equal(oldPort, _api.Settings.Settings.Server.Port);
        Assert.Equal(48000, JsonNode.Parse(File.ReadAllText(_api.Settings.SettingsFile))!["Server"]!["Port"]!.GetValue<int>());
    }

    [Fact]
    public async Task Llm_connection_test_returns_models()
    {
        var (status, result) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/settings/test-llm", new { provider = "mock" });
        Assert.Equal(200, status);
        Assert.True(result!["reachable"]!.GetValue<bool>());

        var (downStatus, down) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/settings/test-llm", new { provider = "ollama", baseUrl = "http://127.0.0.1:1" });
        Assert.Equal(200, downStatus);
        Assert.False(down!["reachable"]!.GetValue<bool>());
        Assert.Equal(400, (await _admin.JsonAsync(HttpMethod.Post, "/admin/api/settings/test-llm", new { provider = "gpt" })).Status);
    }

    [Fact]
    public async Task Log_tail_filters_by_level_and_keeps_multiline_entries()
    {
        Directory.CreateDirectory(_api.Settings.LogsDir);
        File.WriteAllText(Path.Combine(_api.Settings.LogsDir, "efmailagent-20261126.log"), """
            2026-11-26 10:00:00.000 +01:00 [DBG] EfMailAgent.X: részlet
            2026-11-26 10:00:01.000 +01:00 [INF] EfMailAgent.Api: Új feladat: 1
            2026-11-26 10:00:02.000 +01:00 [ERR] EfMailAgent.Processing.JobProcessor: Feldolgozás sikertelen
            System.Exception: hiba
               at Valami.Hely()
            2026-11-26 10:00:03.000 +01:00 [WRN] EfMailAgent.Rules.RuleRepository: Szabály figyelmeztetés

            """);

        var info = await _admin.GetJsonAsync("/admin/api/logs?level=INF");
        Assert.Equal(["INF", "ERR", "WRN"], info["entries"]!.AsArray().Select(e => e!["level"]!.GetValue<string>()));
        Assert.Contains("at Valami.Hely()", info["entries"]![1]!["message"]!.GetValue<string>());

        var errors = await _admin.GetJsonAsync("/admin/api/logs?level=ERR");
        Assert.Single(errors["entries"]!.AsArray());
    }

    [Fact]
    public async Task Job_details_contain_prompt_and_rows_and_retry_works()
    {
        var submitted = await (await _api.SubmitAsync(ApiFixture.Payload())).Content.ReadFromJsonAsync<JsonNode>();
        var id = submitted!["jobId"]!.GetValue<string>();
        await JobWorkerTests.WaitUntil(() => JobStatus.IsTerminal(_api.Queue.Get(id)!.Status), TimeSpan.FromSeconds(10));

        var list = (await _admin.GetJsonAsync("/admin/api/jobs?status=needs_review")).AsArray();
        Assert.Equal(id, list.Single()!["id"]!.GetValue<string>());

        var detail = await _admin.GetJsonAsync($"/admin/api/jobs/{id}");
        Assert.Contains("## A kinyerendő mezők", detail["promptSystem"]!.GetValue<string>());
        Assert.Equal(5, detail["parsedRows"]!.AsArray().Count);
        Assert.NotNull(detail["rawResponse"]);

        var (retry, _) = await _admin.JsonAsync(HttpMethod.Post, $"/admin/api/jobs/{id}/retry");
        Assert.Equal(200, retry);
        Assert.Equal(404, (await _admin.JsonAsync(HttpMethod.Get, "/admin/api/jobs/nincs")).Status);
    }

    /// <summary>Step 6 acceptance: change the prompt in the UI, test it, and the next real email uses it.</summary>
    [Fact]
    public async Task Prompt_changed_in_the_ui_is_used_by_the_test_console_and_the_next_job()
    {
        var prompt = (await _admin.GetJsonAsync("/admin/api/rules/eloreendeles/file?path=prompt.md"))["content"]!.GetValue<string>();
        const string marker = "- ÚJ UTASÍTÁS: a cikkszámot mindig nagybetűvel írd.";
        var (saved, _) = await _admin.JsonAsync(HttpMethod.Put, "/admin/api/rules/eloreendeles/file?path=prompt.md", new { content = prompt + "\n" + marker + "\n" });
        Assert.Equal(200, saved);

        var (_, test) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/test", new { ruleId = "eloreendeles", subject = "Előrendelés", from = "x", body = "10 db bot" });
        Assert.Contains(marker, test!["prompt"]!["system"]!.GetValue<string>());

        var submitted = await (await _api.SubmitAsync(ApiFixture.Payload(messageId: "<next@x>"))).Content.ReadFromJsonAsync<JsonNode>();
        var id = submitted!["jobId"]!.GetValue<string>();
        await JobWorkerTests.WaitUntil(() => JobStatus.IsTerminal(_api.Queue.Get(id)!.Status), TimeSpan.FromSeconds(10));
        Assert.Contains(marker, _api.Queue.Get(id)!.PromptSystem);
    }
}
