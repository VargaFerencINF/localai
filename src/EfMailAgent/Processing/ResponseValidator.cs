using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EfMailAgent.Rules;
using Json.Schema;

namespace EfMailAgent.Processing;

public static class RowStatus
{
    public const string Ok = "ok";
    public const string NeedsReview = "ellenorzendo";
}

public sealed record ExtractedRow(JsonObject Values, string Status, IReadOnlyList<string> ReviewReasons);

public sealed record ValidationOutcome(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<ExtractedRow> Rows)
{
    public static ValidationOutcome Invalid(params string[] errors) => new(false, errors, []);
}

/// <summary>
/// Parses the model output, validates it against the rule's JSON Schema (JsonSchema.Net) plus checks the schema cannot
/// express (real calendar dates), and assigns row statuses (CLAUDE.md section 8).
/// </summary>
public static partial class ResponseValidator
{
    public const string ConfidenceColumn = "bizonyossag";
    public const string LowConfidence = "alacsony";

    private static readonly HashSet<string> AggregateKeywords = ["properties", "items", "prefixItems", "allOf", "anyOf", "oneOf", "$ref"];

    [GeneratedRegex(@"^\s*```(?:json)?\s*\n(?<json>.*)\n\s*```\s*$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeFence();

    public static ValidationOutcome Validate(Rule rule, string content) => Validate(rule.Output, RuleSchemaBuilder.Build(rule), content);

    public static ValidationOutcome Validate(RuleOutput output, JsonObject schemaNode, string content)
    {
        // Some models wrap JSON in a Markdown code fence despite the instructions; that is harmless to unwrap.
        var text = content.Trim();
        var fence = CodeFence().Match(text);
        if (fence.Success)
            text = fence.Groups["json"].Value;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return ValidationOutcome.Invalid($"A válasz nem érvényes JSON: {ex.Message}");
        }

        using (document)
        {
            var schema = JsonSchema.FromText(schemaNode.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            var errors = new List<string>();
            if (!result.IsValid)
            {
                var all = (result.Details ?? [])
                    .Where(d => d.Errors is not null)
                    .SelectMany(d => d.Errors!.Select(e => (Location: Location(d.InstanceLocation.ToString()), Keyword: e.Key, Message: e.Value)))
                    .ToList();
                // "properties"/"items" only say that something below failed; the specific errors are more useful for the repair call.
                var specific = all.Where(e => !AggregateKeywords.Contains(e.Keyword)).ToList();
                errors.AddRange((specific.Count > 0 ? specific : all).Select(e => $"{e.Location}: {e.Message} ({e.Keyword})"));
                if (errors.Count == 0)
                    errors.Add("A válasz nem felel meg a sémának.");
                return new ValidationOutcome(false, errors.Distinct().ToArray(), []);
            }

            var root = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            var rowsNode = root[output.RowsPath]!.AsArray();

            // Checks beyond JSON Schema: dates must be real calendar dates in yyyy-MM-dd form.
            for (var i = 0; i < rowsNode.Count; i++)
            {
                var row = rowsNode[i]!.AsObject();
                foreach (var column in output.Columns.Where(c => c.Type == ColumnType.Date))
                {
                    if (row[column.Name] is JsonValue value && value.TryGetValue<string>(out var date)
                        && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        errors.Add($"/{output.RowsPath}/{i}/{column.Name}: \"{date}\" nem érvényes ÉÉÉÉ-HH-NN dátum");
                    }
                }
            }
            if (errors.Count > 0)
                return new ValidationOutcome(false, errors, []);

            var rows = rowsNode.Select(r => ClassifyRow(output, r!.AsObject())).ToArray();
            return new ValidationOutcome(true, [], rows);
        }
    }

    /// <summary>ellenorzendo if an enum column named "bizonyossag" says "alacsony" or a required column is empty.</summary>
    public static ExtractedRow ClassifyRow(RuleOutput output, JsonObject row)
    {
        var reasons = new List<string>();
        foreach (var column in output.Columns)
        {
            var value = row[column.Name];
            if (column.Required && IsEmpty(value))
                reasons.Add($"A kötelező '{column.Name}' mező üres.");

            if (column.Type == ColumnType.Enum
                && string.Equals(column.Name, ConfidenceColumn, StringComparison.Ordinal)
                && value is JsonValue v && v.TryGetValue<string>(out var confidence)
                && string.Equals(confidence, LowConfidence, StringComparison.Ordinal))
            {
                reasons.Add("Alacsony bizonyosság.");
            }
        }
        return new ExtractedRow((JsonObject)row.DeepClone(), reasons.Count == 0 ? RowStatus.Ok : RowStatus.NeedsReview, reasons);
    }

    private static bool IsEmpty(JsonNode? value) =>
        value is null || (value is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s));

    private static string Location(string pointer) => string.IsNullOrEmpty(pointer) ? "(gyökér)" : pointer;
}
