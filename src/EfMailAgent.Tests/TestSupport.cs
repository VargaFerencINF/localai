namespace EfMailAgent.Tests;

public static class TestPaths
{
    /// <summary>Shared test vectors and samples are linked into the test output folder by the .csproj.</summary>
    public static string Shared(string file) => Path.Combine(AppContext.BaseDirectory, "shared", file);

    public static string SamplesRoot => Path.Combine(AppContext.BaseDirectory, "samples");

    public static string SampleRules => Path.Combine(SamplesRoot, "rules");

    public static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
}

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "efmailagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Builds rule folders for tests.</summary>
public static class RuleFolderBuilder
{
    public const string ValidYaml = """
        id: {ID}
        name: Teszt szabály
        enabled: true
        priority: 10
        match:
          subject_contains: ["teszt"]
        prompt_file: prompt.md
        output:
          mode: append
          file: "teszt_{yyyy}.csv"
          columns:
            - { name: nev, type: string, required: true, description: "Név" }
            - { name: db, type: integer, required: false, description: "Darab" }
        """;

    public static string Create(TempDir root, string folder, string? yaml = null, bool withPrompt = true)
    {
        var dir = root.Combine("rules", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "rule.yaml"), (yaml ?? ValidYaml).Replace("{ID}", folder));
        if (withPrompt)
            File.WriteAllText(Path.Combine(dir, "prompt.md"), "Teszt prompt");
        return dir;
    }
}
