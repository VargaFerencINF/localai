using EfMailAgent.Processing;
using EfMailAgent.Rules;
using EfMailAgent.Tests.Fakes;

namespace EfMailAgent.Tests.Processing;

public class ResponseValidatorTests
{
    private static readonly Rule SampleRule = RuleLoader.LoadAll(TestPaths.SampleRules).FindById("eloreendeles")!;

    [Fact]
    public void Mock_default_answer_is_valid()
    {
        var content = File.ReadAllText(Path.Combine(TestPaths.SamplesRoot, "mock-responses", "eloreendeles", "default.json"));
        var outcome = ResponseValidator.Validate(SampleRule, content);

        Assert.True(outcome.IsValid, string.Join("\n", outcome.Errors));
        Assert.Equal(5, outcome.Rows.Count);
        Assert.Equal(["ok", "ok", "ok", "ok", "ellenorzendo"], outcome.Rows.Select(r => r.Status));
    }

    [Fact]
    public void Mock_invalid_scenario_is_invalid_with_located_errors()
    {
        var content = File.ReadAllText(Path.Combine(TestPaths.SamplesRoot, "mock-responses", "_scenarios", "invalid.json"));
        var outcome = ResponseValidator.Validate(SampleRule, content);

        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Errors, e => e.StartsWith("/rows/0/mennyiseg"));
        Assert.Contains(outcome.Errors, e => e.StartsWith("/rows/0/bizonyossag"));
        Assert.Contains(outcome.Errors, e => e.Contains("required"));
    }

    [Fact]
    public void Markdown_code_fence_is_unwrapped()
    {
        var outcome = ResponseValidator.Validate(SampleRule, "```json\n" + LlmAnswers.ValidOneRow + "\n```");
        Assert.True(outcome.IsValid, string.Join("\n", outcome.Errors));
    }

    [Theory]
    [InlineData("nem json", "nem érvényes JSON")]
    [InlineData("""{"sorok": []}""", "required")]
    [InlineData("""{"rows": [], "extra": 1}""", "additionalProperties")]
    public void Structural_errors_are_reported(string content, string expected)
    {
        var outcome = ResponseValidator.Validate(SampleRule, content);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Impossible_calendar_date_is_rejected()
    {
        var outcome = ResponseValidator.Validate(SampleRule, LlmAnswers.ValidOneRow.Replace("2027-03-01", "2027-02-30"));
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Errors, e => e.Contains("/rows/0/kert_szallitasi_datum"));
    }

    [Fact]
    public void Empty_required_value_or_low_confidence_needs_review()
    {
        var outcome = ResponseValidator.Validate(SampleRule,
            LlmAnswers.Rows(LlmAnswers.Row(partner: ""), LlmAnswers.Row("alacsony"), LlmAnswers.Row("kozepes")));

        Assert.True(outcome.IsValid);
        Assert.Equal(RowStatus.NeedsReview, outcome.Rows[0].Status);
        Assert.Contains(outcome.Rows[0].ReviewReasons, r => r.Contains("partner_nev"));
        Assert.Equal(RowStatus.NeedsReview, outcome.Rows[1].Status);
        Assert.Equal(RowStatus.Ok, outcome.Rows[2].Status);
    }
}
