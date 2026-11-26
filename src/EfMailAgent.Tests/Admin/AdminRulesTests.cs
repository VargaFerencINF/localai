using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Tests.Api;

namespace EfMailAgent.Tests.Admin;

public class AdminRulesTests : IAsyncLifetime
{
    private ApiFixture _api = null!;
    private AdminTestClient _admin = null!;

    private string RuleDir => Path.Combine(_api.Settings.RulesDir, "eloreendeles");

    public async Task InitializeAsync()
    {
        _api = await ApiFixture.StartAsync();
        _admin = new AdminTestClient(_api);
        await _admin.LoginAsync();
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Rules_list_contains_the_base_prompt_and_the_rule_with_its_files()
    {
        var rules = (await _admin.GetJsonAsync("/admin/api/rules")).AsArray();

        Assert.True(rules[0]!["isBase"]!.GetValue<bool>());
        var rule = rules.Single(r => r!["folder"]!.GetValue<string>() == "eloreendeles")!;
        Assert.True(rule["valid"]!.GetValue<bool>());
        Assert.Equal(["rule.yaml", "prompt.md", "skills/mertekegysegek.md", "skills/partnerek.md"],
            rule["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        Assert.Equal(["rule", "prompt", "skill", "skill"], rule["files"]!.AsArray().Select(f => f!["kind"]!.GetValue<string>()));

        var file = await _admin.GetJsonAsync("/admin/api/rules/eloreendeles/file?path=prompt.md");
        Assert.Contains("Feladat: előrendelés feldolgozása", file["content"]!.GetValue<string>());
        var system = await _admin.GetJsonAsync("/admin/api/rules/_base/file?path=system.md");
        Assert.Contains("adatkinyerő motor", system["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_rule_yaml_is_rejected_with_hungarian_errors_and_nothing_is_written()
    {
        var path = Path.Combine(RuleDir, "rule.yaml");
        var before = File.ReadAllText(path);

        var (status, body) = await _admin.JsonAsync(HttpMethod.Put, "/admin/api/rules/eloreendeles/file?path=rule.yaml",
            new { content = before.Replace("type: number, required: true", "type: decimal, required: true") });

        Assert.Equal(422, status);
        Assert.Contains(body!["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("típusa ismeretlen: 'decimal'"));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));

        var (yamlStatus, yamlBody) = await _admin.JsonAsync(HttpMethod.Put, "/admin/api/rules/eloreendeles/file?path=rule.yaml", new { content = "id: [hibás" });
        Assert.Equal(422, yamlStatus);
        Assert.Contains("Hibás rule.yaml", yamlBody!["errors"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Saving_a_prompt_keeps_a_backup_and_reloads_the_rules()
    {
        var path = Path.Combine(RuleDir, "prompt.md");
        var before = File.ReadAllText(path);
        var version = _api.App.Services.GetRequiredServiceRules().Current.Version;

        var (status, body) = await _admin.JsonAsync(HttpMethod.Put, "/admin/api/rules/eloreendeles/file?path=prompt.md", new { content = before + "\n- Új szabály a UI-ból.\n" });

        Assert.Equal(200, status);
        Assert.True(body!["saved"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllText(path + ".bak"));
        Assert.Contains("Új szabály a UI-ból", File.ReadAllText(path));
        var current = _api.App.Services.GetRequiredServiceRules().Current;
        Assert.NotEqual(version, current.Version);
        Assert.Contains("Új szabály a UI-ból", current.FindById("eloreendeles")!.PromptText);
    }

    [Theory]
    [InlineData("eloreendeles", "../../appsettings.json", 400)]
    [InlineData("eloreendeles", "../masik/rule.yaml", 400)]
    [InlineData("eloreendeles", "virus.exe", 400)]
    [InlineData("nincs-ilyen", "rule.yaml", 404)]
    [InlineData("..", "rule.yaml", 404)]
    public async Task Paths_outside_rule_folders_are_refused(string folder, string path, int expected)
    {
        var (status, _) = await _admin.JsonAsync(HttpMethod.Put, $"/admin/api/rules/{Uri.EscapeDataString(folder)}/file?path={Uri.EscapeDataString(path)}", new { content = "x" });
        Assert.Equal(expected, status);
        var (readStatus, _) = await _admin.JsonAsync(HttpMethod.Get, $"/admin/api/rules/{Uri.EscapeDataString(folder)}/file?path={Uri.EscapeDataString(path)}");
        Assert.Equal(expected, readStatus);
    }

    [Fact]
    public async Task Enabled_toggle_rewrites_only_that_line_and_affects_matchers()
    {
        var (status, _) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules/eloreendeles/enabled", new { enabled = false });

        Assert.Equal(200, status);
        var yaml = File.ReadAllText(Path.Combine(RuleDir, "rule.yaml"));
        Assert.Contains("enabled: false", yaml);
        Assert.Contains("priority: 10                 # lower number wins", yaml); // comments preserved
        var matchers = (await (await _api.Client.GetAsync("/api/matchers")).Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Empty(matchers["matchers"]!.AsArray());
    }

    [Fact]
    public async Task Skill_files_can_be_added_and_removed()
    {
        var (addStatus, added) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules/eloreendeles/skills", new { fileName = "cikklista.md", content = "Feeder bot;EF-B3600" });
        Assert.Equal(200, addStatus);
        Assert.Equal("skills/cikklista.md", added!["path"]!.GetValue<string>());
        var rule = _api.App.Services.GetRequiredServiceRules().Current.FindById("eloreendeles")!;
        Assert.Equal(["mertekegysegek.md", "partnerek.md", "cikklista.md"], rule.Skills.Select(s => s.FileName));

        var (badStatus, bad) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules/eloreendeles/skills", new { fileName = "../x.md", content = "" });
        Assert.Equal(422, badStatus);
        Assert.Contains("skill fájl neve", bad!["errors"]![0]!.GetValue<string>());

        var (removeStatus, _) = await _admin.JsonAsync(HttpMethod.Delete, "/admin/api/rules/eloreendeles/file?path=skills/partnerek.md");
        Assert.Equal(200, removeStatus);
        Assert.False(File.Exists(Path.Combine(RuleDir, "skills", "partnerek.md")));
        Assert.True(File.Exists(Path.Combine(RuleDir, "skills", "partnerek.md.bak")));
        rule = _api.App.Services.GetRequiredServiceRules().Current.FindById("eloreendeles")!;
        Assert.Equal(["mertekegysegek.md", "cikklista.md"], rule.Skills.Select(s => s.FileName));

        var (promptStatus, _) = await _admin.JsonAsync(HttpMethod.Delete, "/admin/api/rules/eloreendeles/file?path=prompt.md");
        Assert.Equal(422, promptStatus);
    }

    [Fact]
    public async Task New_rule_from_template_is_valid_and_disabled()
    {
        var (status, _) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules", new { id = "arajanlat", name = "Árajánlatkérés \"teszt\": feldolgozás" });

        Assert.Equal(200, status);
        var rule = _api.App.Services.GetRequiredServiceRules().Current.FindById("arajanlat")!;
        Assert.Equal("Árajánlatkérés \"teszt\": feldolgozás", rule.Name);
        Assert.False(rule.Enabled);
        Assert.Equal("arajanlat_{yyyy}-{MM}.csv", rule.Output.FilePattern);

        var (duplicate, body) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules", new { id = "arajanlat", name = "x" });
        Assert.Equal(422, duplicate);
        Assert.Contains("Már létezik", body!["errors"]![0]!.GetValue<string>());
        Assert.Equal(422, (await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules", new { id = "Nagy Betű", name = "x" })).Status);
    }

    [Fact]
    public async Task Duplicated_rule_gets_new_id_name_and_csv_file_and_starts_disabled()
    {
        var (status, _) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules", new { id = "eloreendeles-b2b", name = "Előrendelés (B2B)", copyFrom = "eloreendeles" });

        Assert.Equal(200, status);
        var set = _api.App.Services.GetRequiredServiceRules().Current;
        var copy = set.FindById("eloreendeles-b2b")!;
        Assert.Equal("Előrendelés (B2B)", copy.Name);
        Assert.False(copy.Enabled);
        Assert.Equal("eloreendeles-b2b_{yyyy}-{MM}.csv", copy.Output.FilePattern);
        Assert.Equal(2, copy.Skills.Count);
        Assert.True(set.FindById("eloreendeles")!.Enabled);
        Assert.Empty(set.InvalidFolders);
    }

    [Fact]
    public async Task Output_file_and_mode_can_be_set_per_rule_and_invalid_names_are_refused()
    {
        var rule = (await _admin.GetJsonAsync("/admin/api/rules")).AsArray().Single(r => r!["folder"]!.GetValue<string>() == "eloreendeles")!;
        Assert.Equal("append", rule["outputMode"]!.GetValue<string>());
        Assert.Equal("eloreendeles_{yyyy}-{MM}.csv", rule["outputFile"]!.GetValue<string>());

        var (status, _) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules/eloreendeles/output", new { mode = "per_job", file = "rendeles_{yyyy}{MM}{dd}_{jobId}.csv" });
        Assert.Equal(200, status);
        rule = (await _admin.GetJsonAsync("/admin/api/rules")).AsArray().Single(r => r!["folder"]!.GetValue<string>() == "eloreendeles")!;
        Assert.Equal("per_job", rule["outputMode"]!.GetValue<string>());
        Assert.Equal("rendeles_{yyyy}{MM}{dd}_{jobId}.csv", rule["outputFile"]!.GetValue<string>());
        Assert.Contains("columns:", File.ReadAllText(Path.Combine(RuleDir, "rule.yaml")));

        foreach (var (mode, file, expected) in new[]
        {
            ("append", "..\\..\\x.csv", "nem tartalmazhat mappát"),
            ("append", "a_{valami}.csv", "Ismeretlen helyettesítő"),
            ("append", " ", "Add meg a CSV fájl nevét"),
            ("hetente", "a.csv", "append"),
        })
        {
            var (code, body) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/rules/eloreendeles/output", new { mode, file });
            Assert.True(code >= 400, $"{file}: {code}");
            Assert.Contains(expected, body!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        Assert.Contains("rendeles_{yyyy}{MM}{dd}_{jobId}.csv", File.ReadAllText(Path.Combine(RuleDir, "rule.yaml"))); // unchanged by the refused saves
    }
}

