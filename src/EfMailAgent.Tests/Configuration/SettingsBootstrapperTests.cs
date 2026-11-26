using System.Text.Json.Nodes;
using EfMailAgent.Configuration;

namespace EfMailAgent.Tests.Configuration;

public class SettingsBootstrapperTests
{
    [Fact]
    public void First_run_creates_structure_settings_token_and_copies_samples()
    {
        using var tmp = new TempDir();
        var dataDir = new DataDirectory(tmp.Combine("data"), DataDirectorySource.CommandLine);

        var (settings, report) = SettingsBootstrapper.Initialize(dataDir, TestPaths.SamplesRoot);

        Assert.True(report.SettingsCreated);
        Assert.True(report.TokenGenerated);
        Assert.NotNull(report.SamplesCopiedFrom);
        foreach (var dir in new[] { "rules", "output", "data", "logs", "addon" })
            Assert.True(Directory.Exists(tmp.Combine("data", dir)), dir);
        Assert.True(File.Exists(tmp.Combine("data", "rules", "eloreendeles", "rule.yaml")));
        Assert.True(File.Exists(tmp.Combine("data", "rules", "_base", "system.md")));

        var s = settings.Settings;
        Assert.Equal(47811, s.Server.Port);
        Assert.Equal(43, s.Server.Token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", s.Server.Token);
        Assert.Equal(LlmProviders.Mock, s.Llm.Provider);
        Assert.True(Directory.Exists(s.Llm.MockDir));
        Assert.Equal(300, s.Llm.TimeoutSeconds);
        Assert.Equal(";", s.Csv.Delimiter);
        Assert.Equal(",", s.Csv.DecimalSeparator);
        Assert.Equal(CsvEncodings.Utf8Bom, s.Csv.Encoding);
        Assert.Equal("Europe/Budapest", s.App.TimeZone);

        var bytes = File.ReadAllBytes(settings.SettingsFile);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "appsettings.json must not have a BOM");
    }

    [Fact]
    public void Existing_settings_are_not_overwritten_and_rules_not_copied_again()
    {
        using var tmp = new TempDir();
        var dataDir = new DataDirectory(tmp.Combine("data"), DataDirectorySource.CommandLine);
        var (first, _) = SettingsBootstrapper.Initialize(dataDir, TestPaths.SamplesRoot);

        var json = JsonNode.Parse(File.ReadAllText(first.SettingsFile))!;
        json["Server"]!["Port"] = 50000;
        File.WriteAllText(first.SettingsFile, json.ToJsonString());
        File.Delete(tmp.Combine("data", "rules", "eloreendeles", "prompt.md"));

        var (second, report) = SettingsBootstrapper.Initialize(dataDir, TestPaths.SamplesRoot);

        Assert.False(report.SettingsCreated);
        Assert.False(report.TokenGenerated);
        Assert.Null(report.SamplesCopiedFrom);
        Assert.Equal(50000, second.Settings.Server.Port);
        Assert.Equal(first.Settings.Server.Token, second.Settings.Server.Token);
        Assert.False(File.Exists(tmp.Combine("data", "rules", "eloreendeles", "prompt.md")));
    }

    [Fact]
    public void Empty_token_is_generated_and_other_values_kept()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("data/appsettings.json", """
            {
              // written by the installer
              "Server": { "Port": 47900, "Token": "" },
              "Llm": { "Provider": "ollama", "BaseUrl": "http://127.0.0.1:11434", "Model": "qwen3.5:9b" }
            }
            """);
        var dataDir = new DataDirectory(tmp.Combine("data"), DataDirectorySource.CommandLine);

        var (settings, report) = SettingsBootstrapper.Initialize(dataDir, TestPaths.SamplesRoot, copySampleRules: false);

        Assert.True(report.TokenGenerated);
        Assert.Equal(43, settings.Settings.Server.Token.Length);
        Assert.Equal(47900, settings.Settings.Server.Port);
        Assert.Equal("qwen3.5:9b", settings.Settings.Llm.Model);
        Assert.Equal(16384, settings.Settings.Llm.NumCtx);
    }

    [Fact]
    public void Invalid_settings_produce_hungarian_errors()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("data/appsettings.json", """
            { "Server": { "Port": 0, "Token": "x" }, "Llm": { "Provider": "gpt" }, "Csv": { "Delimiter": ";;", "DecimalSeparator": ";" }, "App": { "TimeZone": "Mars/Olympus" } }
            """);
        var dataDir = new DataDirectory(tmp.Combine("data"), DataDirectorySource.CommandLine);

        var ex = Assert.Throws<SettingsLoadException>(() => SettingsBootstrapper.Initialize(dataDir, TestPaths.SamplesRoot));

        Assert.Contains(ex.Errors, e => e.StartsWith("Server.Port érvénytelen"));
        Assert.Contains(ex.Errors, e => e.StartsWith("Server.Token hiányzik vagy túl rövid"));
        Assert.Contains(ex.Errors, e => e.StartsWith("Llm.Provider ismeretlen: 'gpt'"));
        Assert.Contains(ex.Errors, e => e.StartsWith("Csv.Delimiter egyetlen karakter"));
        Assert.Contains(ex.Errors, e => e.StartsWith("App.TimeZone ismeretlen időzóna"));
    }

    [Fact]
    public void Malformed_json_is_reported_not_overwritten()
    {
        using var tmp = new TempDir();
        var file = tmp.WriteFile("data/appsettings.json", "{ \"Server\": ");
        var dataDir = new DataDirectory(tmp.Combine("data"), DataDirectorySource.CommandLine);

        var ex = Assert.Throws<SettingsLoadException>(() => SettingsBootstrapper.Initialize(dataDir, TestPaths.SamplesRoot));

        Assert.Contains("nem érvényes JSON", ex.Errors[0]);
        Assert.Equal("{ \"Server\": ", File.ReadAllText(file));
    }

    [Fact]
    public void Production_data_dir_defaults_to_ollama()
    {
        var dataDir = new DataDirectory(DataDirectory.ProductionDefaultPath, DataDirectorySource.ProductionDefault);

        var defaults = SettingsBootstrapper.CreateDefaults(dataDir, TestPaths.SamplesRoot);

        Assert.Equal(LlmProviders.Ollama, defaults.Llm.Provider);
        Assert.Equal("", defaults.Llm.MockDir);
    }

    [Fact]
    public void Relative_paths_resolve_against_data_dir()
    {
        using var tmp = new TempDir();
        var settings = new EffectiveSettings(new DataDirectory(tmp.Path, DataDirectorySource.CommandLine),
            new AppSettings { Paths = { Output = "kimenet", Logs = tmp.Combine("abs-logs") } });

        Assert.Equal(tmp.Combine("kimenet"), settings.OutputDir);
        Assert.Equal(tmp.Combine("abs-logs"), settings.LogsDir);
        Assert.Equal(tmp.Combine("rules"), settings.RulesDir);
    }
}
