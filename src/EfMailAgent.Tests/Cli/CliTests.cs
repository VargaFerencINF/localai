using EfMailAgent.Cli;

namespace EfMailAgent.Tests.Cli;

[Collection("Console")]
public class CliTests
{
    [Fact]
    public async Task Validate_rules_lists_sample_rule_as_valid()
    {
        using var tmp = new TempDir();
        SeedRules(tmp);
        var (code, stdout, _) = await Run("validate-rules", "--data-dir", tmp.Combine("data"));

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("eloreendeles", stdout);
        Assert.Contains("Előrendelés feldolgozás", stdout);
        Assert.Contains("érvényes", stdout);
        Assert.Contains("Összesen: 1 érvényes, 0 hibás szabály.", stdout);
    }

    [Fact]
    public async Task Validate_rules_returns_error_code_for_broken_rule()
    {
        using var tmp = new TempDir();
        SeedRules(tmp);
        Directory.CreateDirectory(tmp.Combine("data", "rules", "torott"));
        File.WriteAllText(tmp.Combine("data", "rules", "torott", "rule.yaml"), "id: torott\n");

        var (code, stdout, _) = await Run("validate-rules", "--data-dir", tmp.Combine("data"));

        Assert.Equal(ExitCodes.InvalidRules, code);
        Assert.Contains("HIBÁS", stdout);
        Assert.Contains("Hiányzik a 'name' mező.", stdout);
        Assert.Contains("Összesen: 1 érvényes, 1 hibás szabály.", stdout);
    }

    [Fact]
    public async Task Print_token_prints_only_the_token()
    {
        using var tmp = new TempDir();
        SeedRules(tmp);
        var (code, stdout, _) = await Run("print-token", "--data-dir", tmp.Combine("data"));

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Matches("^[A-Za-z0-9_-]{43}\\r?\\n$", stdout);
    }

    [Fact]
    public async Task Invalid_settings_exit_with_configuration_error()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("data/appsettings.json", "{ \"Server\": { \"Port\": 70000, \"Token\": \"" + new string('a', 43) + "\" } }");

        var (code, _, stderr) = await Run("print-token", "--data-dir", tmp.Combine("data"));

        Assert.Equal(ExitCodes.ConfigurationError, code);
        Assert.Contains("Server.Port érvénytelen", stderr);
    }

    [Fact]
    public async Task Unknown_command_prints_help_and_fails()
    {
        var (code, stdout, stderr) = await Run("nincs-ilyen-parancs");

        Assert.NotEqual(0, code);
        Assert.Contains("validate-rules", stdout + stderr);
        Assert.Contains("print-token", stdout + stderr);
    }

    /// <summary>Rules folder is pre-filled so tests do not depend on locating the repository's samples.</summary>
    private static void SeedRules(TempDir tmp) =>
        EfMailAgent.Configuration.SettingsBootstrapper.CopyDirectory(TestPaths.SampleRules, tmp.Combine("data", "rules"));

    private static async Task<(int Code, string Stdout, string Stderr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}

[Collection("Console")]
public class TestRuleCliTests
{
    [Fact]
    public async Task Dry_run_prints_prompt_answer_and_rows_and_writes_nothing()
    {
        using var tmp = new TempDir();
        var data = PrepareMockData(tmp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["test-rule", "eloreendeles", "--file", Demo, "--data-dir", data], stdout, stderr);

        var text = stdout.ToString();
        Assert.True(code == ExitCodes.Ok, stderr.ToString() + text);
        Assert.Contains("===== SYSTEM =====", text);
        Assert.Contains("===== USER =====", text);
        Assert.Contains("===== NYERS VÁLASZ (mock", text);
        Assert.Contains("Érvényes válasz, 5 sor, a feladat állapota ez lenne: needs_review", text);
        Assert.Contains("5. sor  [ellenorzendo]", text);
        Assert.Empty(Directory.GetFiles(Path.Combine(data, "output")));
        Assert.False(Directory.Exists(Path.Combine(data, "data", "mock-prompts")));
    }

    [Fact]
    public async Task Write_runs_through_the_queue_and_dedups()
    {
        using var tmp = new TempDir();
        var data = PrepareMockData(tmp);
        var stdout = new StringWriter();

        var first = await CliApp.RunAsync(["test-rule", "eloreendeles", "--file", Demo, "--write", "--data-dir", data], stdout, new StringWriter());
        var second = await CliApp.RunAsync(["test-rule", "eloreendeles", "--file", Demo, "--write", "--data-dir", data], stdout, new StringWriter());
        var third = await CliApp.RunAsync(["test-rule", "eloreendeles", "--file", Demo, "--write", "--new-message-id", "--data-dir", data], stdout, new StringWriter());

        Assert.Equal(ExitCodes.Ok, first);
        Assert.Equal(ExitCodes.Ok, second);
        Assert.Equal(ExitCodes.Ok, third);
        var text = stdout.ToString();
        Assert.Contains("Állapot: needs_review, sorok: 5", text);
        Assert.Contains("már szerepel a sorban", text);
        var csv = Assert.Single(Directory.GetFiles(Path.Combine(data, "output")));
        Assert.Equal(11, File.ReadAllLines(csv).Length); // header + 2 x 5 rows
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Unknown_rule_is_a_usage_error()
    {
        using var tmp = new TempDir();
        var data = PrepareMockData(tmp);
        var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["test-rule", "nincs", "--file", Demo, "--data-dir", data], new StringWriter(), stderr);

        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("Nincs ilyen érvényes szabály", stderr.ToString());
    }

    private static string Demo => Path.Combine(TestPaths.SamplesRoot, "emails", "demo_eloreendeles_01.eml");

    private static string PrepareMockData(TempDir tmp)
    {
        var data = tmp.Combine("data");
        EfMailAgent.Configuration.SettingsBootstrapper.CopyDirectory(TestPaths.SampleRules, Path.Combine(data, "rules"));
        tmp.WriteFile("data/appsettings.json", $$"""
            {
              "Server": { "Token": "{{EfMailAgent.Configuration.TokenGenerator.NewToken()}}" },
              "Llm": { "Provider": "mock", "MockDir": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(TestPaths.SamplesRoot, "mock-responses"))}}, "MockDelayMs": 0 }
            }
            """);
        return data;
    }
}
