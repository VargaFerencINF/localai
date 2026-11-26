using EfMailAgent.Admin;
using EfMailAgent.Configuration;
using EfMailAgent.Rules;

namespace EfMailAgent.Tests.Configuration;

/// <summary>EfMailAgent.exe run on its own (no installer, so no samples folder next to it) must still get the sample rules.</summary>
public class EmbeddedSamplesTests
{
    [Fact]
    public void Standalone_first_run_without_samples_folder_uses_the_rules_built_into_the_exe()
    {
        using var tmp = new TempDir();
        var noSamples = tmp.Combine("no-samples-here");
        Directory.CreateDirectory(noSamples);
        var dataDir = new DataDirectory(tmp.Combine("data"), DataDirectorySource.ProductionDefault);

        var (settings, report) = SettingsBootstrapper.Initialize(dataDir, noSamples);

        Assert.Equal(SettingsBootstrapper.EmbeddedSamplesLabel, report.SamplesCopiedFrom);
        Assert.Equal(LlmProviders.Ollama, settings.Settings.Llm.Provider);
        Assert.True(File.Exists(tmp.Combine("data", "rules", "_base", "system.md")));
        var set = RuleLoader.LoadAll(settings.RulesDir);
        Assert.DoesNotContain(set.Issues, i => !i.IsWarning);
        Assert.Contains(set.Rules, r => r.Id == "eloreendeles" && r.Enabled && r.Skills.Count == 2);
    }

    [Fact]
    public void Embedded_rules_and_emails_are_identical_to_the_samples_folder()
    {
        foreach (var folder in new[] { "rules", "emails" })
        {
            var onDisk = Directory.EnumerateFiles(Path.Combine(TestPaths.SamplesRoot, folder), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Path.Combine(TestPaths.SamplesRoot, folder), f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToList();
            Assert.Equal(onDisk, EmbeddedSamples.List(folder));

            using var tmp = new TempDir();
            Assert.Equal(onDisk.Count, EmbeddedSamples.ExtractTo(folder, tmp.Path));
            foreach (var file in onDisk)
                Assert.Equal(File.ReadAllBytes(Path.Combine(TestPaths.SamplesRoot, folder, file)), File.ReadAllBytes(Path.Combine(tmp.Path, file)));
            Assert.Equal(0, EmbeddedSamples.ExtractTo(folder, tmp.Path)); // existing files are never overwritten
        }
    }

    [Fact]
    public void Test_console_samples_are_available_from_the_exe()
    {
        var dir = EmbeddedSamples.ExtractedFolder("emails");

        Assert.NotNull(dir);
        var samples = SampleEmails.Load(dir);
        Assert.Contains(samples, s => s.File == "demo_eloreendeles_01.eml" && s.Subject.Contains("Előrendelés"));
        Assert.True(samples.Count >= 8);
    }
}
