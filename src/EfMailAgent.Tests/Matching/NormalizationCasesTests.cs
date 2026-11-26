using System.Text.Json;
using System.Text.Json.Serialization;
using EfMailAgent.Matching;

namespace EfMailAgent.Tests.Matching;

/// <summary>Runs /shared/normalization-cases.json, the same file the add-on's Node tests use.</summary>
public class NormalizationCasesTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static CasesFile Load() =>
        JsonSerializer.Deserialize<CasesFile>(File.ReadAllText(TestPaths.Shared("normalization-cases.json")), Options)!;

    public static TheoryData<int> NormalizeIndexes() => new(Enumerable.Range(0, Load().Normalize.Count));

    public static TheoryData<int, int> MatchingIndexes()
    {
        var data = new TheoryData<int, int>();
        var file = Load();
        for (var s = 0; s < file.Matching.Count; s++)
            for (var c = 0; c < file.Matching[s].Cases.Count; c++)
                data.Add(s, c);
        return data;
    }

    [Fact]
    public void File_has_enough_cases()
    {
        var file = Load();
        Assert.True(file.Normalize.Count + file.Matching.Sum(m => m.Cases.Count) >= 25);
    }

    [Theory]
    [MemberData(nameof(NormalizeIndexes))]
    public void Normalize_matches_shared_vector(int index)
    {
        var c = Load().Normalize[index];
        Assert.Equal(c.Expected, SubjectNormalizer.Normalize(c.Input));
    }

    [Theory]
    [MemberData(nameof(MatchingIndexes))]
    public void Matching_matches_shared_vector(int suiteIndex, int caseIndex)
    {
        var suite = Load().Matching[suiteIndex];
        var c = suite.Cases[caseIndex];
        var matchers = suite.Rules.Select(r => new SubjectMatcher(r.RuleId, r.Priority, r.Enabled, r.SubjectContains, r.SubjectRegex));

        var best = RuleMatcher.FindBest(c.Subject, matchers);

        Assert.Equal(c.ExpectedRuleId, best?.RuleId);
    }

    [Fact]
    public void Normalize_is_idempotent_for_all_vectors()
    {
        foreach (var c in Load().Normalize)
            Assert.Equal(c.Expected, SubjectNormalizer.Normalize(c.Expected));
    }

    private sealed record CasesFile(List<NormalizeCase> Normalize, List<MatchingSuite> Matching);

    private sealed record NormalizeCase(string Input, string Expected, string? Note);

    private sealed record MatchingSuite(string Name, List<MatcherRule> Rules, List<MatchingCase> Cases);

    private sealed record MatcherRule(string RuleId, bool Enabled, int Priority, List<string> SubjectContains, string? SubjectRegex);

    private sealed record MatchingCase(string Subject, [property: JsonPropertyName("expectedRuleId")] string? ExpectedRuleId, string? Note);
}
