using EfMailAgent.Rules;

namespace EfMailAgent.Tests.Rules;

public class RuleRepositoryTests
{
    [Fact]
    public void Version_is_stable_and_changes_with_content()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "jo");
        var rules = tmp.Combine("rules");

        var v1 = RulesVersion.Compute(rules);
        Assert.Equal(v1, RulesVersion.Compute(rules));
        Assert.Equal(16, v1.Length);

        File.AppendAllText(Path.Combine(rules, "jo", "prompt.md"), " módosítva");
        var v2 = RulesVersion.Compute(rules);
        Assert.NotEqual(v1, v2);

        File.WriteAllText(Path.Combine(rules, "jo", "prompt.md.bak"), "mentés");
        File.WriteAllText(Path.Combine(rules, "jo", "valami.tmp"), "átmeneti");
        Assert.Equal(v2, RulesVersion.Compute(rules));

        File.Move(Path.Combine(rules, "jo", "prompt.md"), Path.Combine(rules, "jo", "prompt2.md"));
        Assert.NotEqual(v2, RulesVersion.Compute(rules));
    }

    [Fact]
    public async Task Hot_reload_picks_up_changes_after_debounce()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "jo");
        using var repository = new RuleRepository(tmp.Combine("rules"), debounce: TimeSpan.FromMilliseconds(200));
        var initial = repository.Start();
        Assert.Single(initial.Rules);

        var reloaded = new TaskCompletionSource<RuleSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Reloaded += (_, set) =>
        {
            if (set.Rules.Count == 2)
                reloaded.TrySetResult(set);
        };

        RuleFolderBuilder.Create(tmp, "masodik");

        var result = await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["jo", "masodik"], result.Rules.Select(r => r.Id));
        Assert.NotEqual(initial.Version, result.Version);
        Assert.Same(result, repository.Current);
    }

    [Fact]
    public async Task Broken_rule_after_reload_is_skipped_without_crashing()
    {
        using var tmp = new TempDir();
        var folder = RuleFolderBuilder.Create(tmp, "jo");
        using var repository = new RuleRepository(tmp.Combine("rules"), debounce: TimeSpan.FromMilliseconds(200));
        repository.Start();

        var reloaded = new TaskCompletionSource<RuleSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Reloaded += (_, set) => reloaded.TrySetResult(set);

        File.WriteAllText(Path.Combine(folder, "rule.yaml"), "id: [hibás");

        var result = await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(result.Rules);
        Assert.Equal(["jo"], result.InvalidFolders);
    }

    [Fact]
    public void Reload_without_changes_keeps_the_same_snapshot()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "jo");
        using var repository = new RuleRepository(tmp.Combine("rules"));
        var first = repository.Start();

        Assert.Same(first, repository.Reload());
    }
}
