using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EfMailAgent.Rules;

namespace EfMailAgent.Processing;

/// <summary>
/// Repairs common format slips of the model before schema validation, so that one badly formatted optional field never
/// loses a whole email: numbers written as text ("54,90 EUR", "18 500 Ft"), Hungarian or dotted dates ("2027. február
/// 20.", "2027.02.20"), enum values with accents or another case, missing optional fields, unknown extra fields.
/// What cannot be repaired in an optional column becomes null, the original text goes into the "megjegyzes" column (if
/// the rule has one) and the row is marked for review. Required columns are left as they are: their errors still go to
/// the repair call.
/// </summary>
public static partial class ResponseNormalizer
{
    public const string NoteColumn = "megjegyzes";

    private static readonly string[] HungarianMonths =
        ["január", "február", "március", "április", "május", "június", "július", "augusztus", "szeptember", "október", "november", "december"];

    [GeneratedRegex(@"(?<y>\d{4})\s*[.\-/]\s*(?<m>[a-záéíóöőúüű]+|\d{1,2})\.?\s*[.\-/]?\s*(?<d>\d{1,2})\.?", RegexOptions.IgnoreCase)]
    private static partial Regex YearFirstDate();

    [GeneratedRegex(@"(?<d>\d{1,2})\s*[./]\s*(?<m>\d{1,2})\s*[./]\s*(?<y>\d{4})")]
    private static partial Regex DayFirstDate();

    [GeneratedRegex(@"-?\d[\d\s  .,']*")]
    private static partial Regex NumberPart();

    /// <summary>Normalizes the rows in place; returns the review reasons per row index.</summary>
    public static Dictionary<int, List<string>> Normalize(RuleOutput output, JsonNode? root)
    {
        var reasons = new Dictionary<int, List<string>>();
        if (root is not JsonObject obj)
            return reasons;
        // Unknown top-level properties next to the rows array are dropped.
        foreach (var key in obj.Select(p => p.Key).Where(k => k != output.RowsPath).ToList())
            obj.Remove(key);
        if (obj[output.RowsPath] is not JsonArray rows)
            return reasons;

        var known = output.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row)
                continue;
            foreach (var extra in row.Select(p => p.Key).Where(k => !known.Contains(k)).ToList())
                row.Remove(extra);
            foreach (var column in output.Columns)
            {
                if (!row.ContainsKey(column.Name))
                {
                    if (!column.Required)
                        row[column.Name] = null; // the model left out an optional field: same as "not in the email"
                    continue;
                }
                var problem = NormalizeValue(column, row);
                if (problem is not null)
                {
                    (reasons.TryGetValue(i, out var list) ? list : reasons[i] = []).Add(problem);
                }
            }
        }
        return reasons;
    }

    /// <summary>Returns a review reason when an optional value had to be emptied.</summary>
    private static string? NormalizeValue(RuleColumn column, JsonObject row)
    {
        var node = row[column.Name];
        if (node is null)
            return null;
        if (node is not JsonValue value)
            return column.Required ? null : Drop(column, row, node.ToJsonString());

        switch (column.Type)
        {
            case ColumnType.String:
                if (!value.TryGetValue<string>(out _))
                    row[column.Name] = node.ToJsonString().Trim('"'); // e.g. an article number given as a number
                return null;

            case ColumnType.Number or ColumnType.Integer:
                if (value.TryGetValue<string>(out var numberText))
                {
                    if (ParseNumber(numberText, column.Type == ColumnType.Integer) is { } number)
                    {
                        row[column.Name] = column.Type == ColumnType.Integer ? JsonValue.Create((long)number) : JsonValue.Create(number);
                        return null;
                    }
                    return column.Required ? null : Drop(column, row, numberText);
                }
                return null;

            case ColumnType.Date:
                if (value.TryGetValue<string>(out var dateText))
                {
                    if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        return null;
                    if (ParseDate(dateText) is { } date)
                    {
                        row[column.Name] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        if (Regex.IsMatch(Strip(dateText, date), @"\p{L}{3,}"))
                            AppendNote(row, $"{column.Name}: „{dateText.Trim()}”"); // keep "előtt", "-ig" and similar wording
                        return null;
                    }
                    return column.Required ? null : Drop(column, row, dateText);
                }
                return column.Required ? null : Drop(column, row, node.ToJsonString());

            case ColumnType.Boolean:
                if (value.TryGetValue<string>(out var boolText))
                {
                    var b = boolText.Trim().ToLowerInvariant();
                    if (b is "igen" or "true" or "1" or "i")
                        row[column.Name] = true;
                    else if (b is "nem" or "false" or "0" or "n")
                        row[column.Name] = false;
                    else
                        return column.Required ? null : Drop(column, row, boolText);
                }
                return null;

            case ColumnType.Enum:
                if (value.TryGetValue<string>(out var enumText))
                {
                    var match = column.Values.FirstOrDefault(v => string.Equals(Fold(v), Fold(enumText), StringComparison.Ordinal));
                    if (match is not null)
                    {
                        row[column.Name] = match;
                        return null;
                    }
                    return column.Required ? null : Drop(column, row, enumText);
                }
                return null;
        }
        return null;
    }

    private static string Drop(RuleColumn column, JsonObject row, string original)
    {
        row[column.Name] = null;
        AppendNote(row, $"{column.Name}: „{original.Trim()}”");
        return $"A(z) '{column.Name}' értéke nem volt értelmezhető („{original.Trim()}”), üresen maradt.";
    }

    private static void AppendNote(JsonObject row, string text)
    {
        if (!row.ContainsKey(NoteColumn))
            return;
        var current = row[NoteColumn] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        if (!current.Contains(text, StringComparison.Ordinal))
            row[NoteColumn] = current.Length == 0 ? text : $"{current}; {text}";
    }

    /// <summary>"54,90", "54.90 EUR", "18 500 Ft", "1.234,5", "1,234.5", "12 db" → number; null if no single number.</summary>
    public static double? ParseNumber(string text, bool integer = false)
    {
        var matches = NumberPart().Matches(text);
        if (matches.Count != 1)
            return null;
        var s = Regex.Replace(matches[0].Value.Trim().TrimEnd('.', ','), @"[\s  ']", "");
        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
            s = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (lastComma >= 0)
            s = s.Count(c => c == ',') > 1 ? s.Replace(",", "") : s.Replace(',', '.');
        else if (s.Count(c => c == '.') > 1 || (lastDot >= 0 && s.Length - lastDot - 1 == 3 && integer))
            s = s.Replace(".", ""); // 1.000.000 or an integer written with a thousands dot
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        if (integer && Math.Abs(number - Math.Round(number)) > 1e-9)
            return null;
        return number;
    }

    /// <summary>"2027. február 20.", "2027.02.20", "2027. 02. 20.", "2027/2/20", "20.02.2027" → date; null otherwise.</summary>
    public static DateOnly? ParseDate(string text)
    {
        var m = YearFirstDate().Match(text);
        if (m.Success)
        {
            var monthText = m.Groups["m"].Value.ToLowerInvariant();
            var month = int.TryParse(monthText, out var mm) ? mm : Array.IndexOf(HungarianMonths, monthText) + 1;
            if (TryDate(m.Groups["y"].Value, month, m.Groups["d"].Value) is { } d1)
                return d1;
        }
        var dm = DayFirstDate().Match(text);
        if (dm.Success && int.TryParse(dm.Groups["m"].Value, out var month2) && TryDate(dm.Groups["y"].Value, month2, dm.Groups["d"].Value) is { } d2)
            return d2;
        return null;
    }

    private static DateOnly? TryDate(string year, int month, string day)
    {
        if (month < 1 || month > 12 || !int.TryParse(year, out var y) || !int.TryParse(day, out var d))
            return null;
        return d >= 1 && d <= DateTime.DaysInMonth(y, month) ? new DateOnly(y, month, d) : null;
    }

    private static string Strip(string text, DateOnly date)
    {
        var withoutDigits = Regex.Replace(text, @"\d", " ");
        return HungarianMonths.Aggregate(withoutDigits, (s, month) => s.Replace(month, " ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lower case without accents: "Közepes" == "kozepes".</summary>
    private static string Fold(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
