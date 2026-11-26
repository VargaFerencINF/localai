using System.Text.RegularExpressions;

namespace EfMailAgent.Matching;

/// <summary>The matching part of a rule, with normalized keywords and a compiled regex.</summary>
public sealed class SubjectMatcher
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public SubjectMatcher(string ruleId, int priority, bool enabled, IEnumerable<string> subjectContains, string? subjectRegex)
    {
        RuleId = ruleId;
        Priority = priority;
        Enabled = enabled;
        SubjectContains = subjectContains.ToArray();
        NormalizedKeywords = SubjectContains.Select(SubjectNormalizer.NormalizeText).Where(k => k.Length > 0).Distinct().ToArray();
        SubjectRegex = string.IsNullOrWhiteSpace(subjectRegex) ? null : subjectRegex;
        Regex = SubjectRegex is null ? null : CreateRegex(SubjectRegex);
    }

    public string RuleId { get; }
    public int Priority { get; }
    public bool Enabled { get; }
    public IReadOnlyList<string> SubjectContains { get; }
    public IReadOnlyList<string> NormalizedKeywords { get; }
    public string? SubjectRegex { get; }
    public Regex? Regex { get; }

    /// <summary>
    /// ECMAScript mode keeps the .NET regex dialect close to JavaScript's, because the add-on evaluates the same
    /// pattern. The pattern is applied to the normalized (lowercase, accent-free) subject.
    /// </summary>
    public static Regex CreateRegex(string pattern) => new(pattern, RegexOptions.ECMAScript, RegexTimeout);

    public bool IsMatch(string normalizedSubject)
    {
        if (!Enabled)
            return false;

        foreach (var keyword in NormalizedKeywords)
        {
            if (normalizedSubject.Contains(keyword, StringComparison.Ordinal))
                return true;
        }

        if (Regex is null)
            return false;
        try
        {
            return Regex.IsMatch(normalizedSubject);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

public static class RuleMatcher
{
    /// <summary>Lowest priority wins; ties broken by rule id (ordinal). Disabled rules never match.</summary>
    public static SubjectMatcher? FindBest(string? subject, IEnumerable<SubjectMatcher> matchers)
    {
        var normalized = SubjectNormalizer.Normalize(subject);
        return matchers
            .Where(m => m.IsMatch(normalized))
            .OrderBy(m => m.Priority)
            .ThenBy(m => m.RuleId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
