using EfMailAgent.Matching;

namespace EfMailAgent.Rules;

/// <summary>An immutable snapshot of all rules, swapped atomically on reload.</summary>
public sealed record RuleSet(
    string RulesDir,
    string Version,
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<RuleIssue> Issues,
    IReadOnlyList<string> InvalidFolders,
    string BaseSystemPrompt,
    DateTimeOffset LoadedAt)
{
    public static RuleSet Empty(string rulesDir) => new(rulesDir, RulesVersion.Compute(rulesDir), [], [], [], "", DateTimeOffset.UtcNow);

    public IEnumerable<Rule> EnabledRules => Rules.Where(r => r.Enabled);

    public Rule? FindById(string id) => Rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    public Rule? FindBestMatch(string? subject)
    {
        var matcher = RuleMatcher.FindBest(subject, Rules.Select(r => r.Matcher));
        return matcher is null ? null : FindById(matcher.RuleId);
    }
}
