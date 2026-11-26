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
        // the missing optional fields are no longer an error: they are treated as "not in the email"
        Assert.DoesNotContain(outcome.Errors, e => e.Contains("required"));
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
    [InlineData("""{"rows": "nem lista"}""", "type")]
    public void Structural_errors_are_reported(string content, string expected)
    {
        var outcome = ResponseValidator.Validate(SampleRule, content);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Impossible_optional_date_is_emptied_kept_in_the_note_and_the_row_needs_review()
    {
        var outcome = ResponseValidator.Validate(SampleRule, LlmAnswers.ValidOneRow.Replace("2027-03-01", "2027-02-30"));

        Assert.True(outcome.IsValid, string.Join("\n", outcome.Errors));
        var row = outcome.Rows[0];
        Assert.Null(row.Values["kert_szallitasi_datum"]);
        Assert.Contains("2027-02-30", row.Values["megjegyzes"]!.GetValue<string>());
        Assert.Equal(RowStatus.NeedsReview, row.Status);
        Assert.Contains(row.ReviewReasons, r => r.Contains("kert_szallitasi_datum"));
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

    [Fact]
    public void Format_slips_of_the_model_are_repaired_instead_of_failing_the_email()
    {
        const string answer = """
            {"rows": [{"partner_nev": "Danubius", "cikk_megnevezes": "Bot", "mennyiseg": "8 db", "mennyisegi_egyseg": "db",
              "egysegar": "54,90 EUR", "penznem": "EUR", "kert_szallitasi_datum": "2027. február 20. előtt", "megjegyzes": null,
              "bizonyossag": "Közepes", "felesleges": 1}], "magyarazat": "kész"}
            """;
        var outcome = ResponseValidator.Validate(SampleRule, answer);

        Assert.True(outcome.IsValid, string.Join("\n", outcome.Errors));
        var row = outcome.Rows[0].Values;
        Assert.Equal(8, row["mennyiseg"]!.GetValue<double>());
        Assert.Equal(54.9, row["egysegar"]!.GetValue<double>());
        Assert.Equal("2027-02-20", row["kert_szallitasi_datum"]!.GetValue<string>());
        Assert.Contains("előtt", row["megjegyzes"]!.GetValue<string>()); // the wording is kept
        Assert.Equal("kozepes", row["bizonyossag"]!.GetValue<string>());
        Assert.Null(row["cikkszam"]); // missing optional field
        Assert.False(row.ContainsKey("felesleges"));
        Assert.Equal(RowStatus.Ok, outcome.Rows[0].Status);
    }

    [Theory]
    [InlineData("18 500 Ft", 18500.0)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("54,90", 54.9)]
    [InlineData("3-4 db", null)]
    [InlineData("tíz", null)]
    public void Numbers_written_as_text(string text, double? expected) => Assert.Equal(expected, ResponseNormalizer.ParseNumber(text));

    [Theory]
    [InlineData("2027. 03. 15.", "2027-03-15")]
    [InlineData("2027.02.20", "2027-02-20")]
    [InlineData("20.02.2027", "2027-02-20")]
    [InlineData("2027. március 1-ig", "2027-03-01")]
    [InlineData("húsvét előtt", null)]
    [InlineData("2027-02-30", null)]
    public void Dates_written_in_other_forms(string text, string? expected) =>
        Assert.Equal(expected, ResponseNormalizer.ParseDate(text)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Unreadable_required_value_still_goes_to_the_repair_call()
    {
        var outcome = ResponseValidator.Validate(SampleRule, LlmAnswers.ValidOneRow.Replace("\"mennyiseg\": 10", "\"mennyiseg\": \"tíz\""));
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Errors, e => e.StartsWith("/rows/0/mennyiseg"));
    }
}

