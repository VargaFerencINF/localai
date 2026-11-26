using EfMailAgent.Matching;

namespace EfMailAgent.Tests.Matching;

public class SubjectMatcherTests
{
    [Fact]
    public void Keyword_with_leading_prefix_like_word_is_not_stripped()
    {
        var matcher = new SubjectMatcher("r", 1, true, ["Re: fontos"], null);
        Assert.Equal(["re: fontos"], matcher.NormalizedKeywords);
    }

    [Fact]
    public void Regex_uses_ecmascript_semantics()
    {
        // In ECMAScript mode \d is ASCII-only, like JavaScript; .NET's default would also match e.g. Arabic-Indic digits.
        var matcher = new SubjectMatcher("r", 1, true, [], "^rma-\\d+");
        Assert.True(matcher.IsMatch("rma-12"));
        Assert.False(matcher.IsMatch("rma-\u0661\u0662"));
    }

    [Fact]
    public void Disabled_matcher_never_matches()
    {
        var matcher = new SubjectMatcher("r", 1, false, ["teszt"], null);
        Assert.False(matcher.IsMatch("teszt"));
    }
}
