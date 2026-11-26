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
