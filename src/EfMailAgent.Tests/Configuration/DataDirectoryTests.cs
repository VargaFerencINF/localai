using EfMailAgent.Configuration;

namespace EfMailAgent.Tests.Configuration;

public class DataDirectoryTests
{
    [Fact]
    public void Command_line_wins_over_environment()
    {
        var d = DataDirectory.Resolve("./cli", "/env", productionBuild: true);
        Assert.Equal(DataDirectorySource.CommandLine, d.Source);
        Assert.Equal(Path.GetFullPath("./cli"), d.Path);
        Assert.True(d.IsDevelopment);
    }

    [Fact]
    public void Environment_variable_is_used_when_no_argument()
    {
        var env = Path.Combine(Path.GetTempPath(), "efenv");
        var d = DataDirectory.Resolve(null, env, productionBuild: true);
        Assert.Equal(DataDirectorySource.EnvironmentVariable, d.Source);
        Assert.Equal(env, d.Path);
    }

    [Fact]
    public void Defaults_depend_on_build_kind()
    {
        Assert.Equal(DataDirectory.ProductionDefaultPath, DataDirectory.Resolve(null, "", productionBuild: true).Path);
        Assert.False(DataDirectory.Resolve(null, "", productionBuild: true).IsDevelopment);

        var dev = DataDirectory.Resolve(null, "", productionBuild: false);
        Assert.Equal(DataDirectorySource.DevelopmentDefault, dev.Source);
        Assert.Equal(Path.GetFullPath(".devdata"), dev.Path);
    }

    [Fact]
    public void Tokens_are_unique_base64url()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => TokenGenerator.NewToken()).ToHashSet();
        Assert.Equal(50, tokens.Count);
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
    }
}
