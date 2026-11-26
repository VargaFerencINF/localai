using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Rules;

namespace EfMailAgent.Output;

/// <summary>Cell formatting and RFC 4180 quoting.</summary>
public sealed class CsvFormatter(char delimiter, string decimalSeparator, bool escapeFormulas = false)
{
    public char Delimiter { get; } = delimiter;

    public string FormatLine(IEnumerable<string> cells) => string.Join(Delimiter, cells.Select(Quote)) + "\r\n";

    /// <summary>Quotes a cell when it contains the delimiter, a quote or a line break; quotes are doubled.</summary>
    public string Quote(string value)
    {
        if (value.IndexOfAny([Delimiter, '"', '\r', '\n']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>OWASP CSV injection guard for text cells.</summary>
    public string ProtectText(string value) =>
        escapeFormulas && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    public string FormatValue(JsonNode? node, ColumnType type)
    {
        if (node is null)
            return "";

        if (node is not JsonValue value)
            return node.ToJsonString();

        switch (type)
        {
            case ColumnType.Number or ColumnType.Integer:
                var element = value.GetValue<JsonElement>();
                if (element.ValueKind != JsonValueKind.Number)
                    return element.ToString();
                return FormatNumber(element);
            case ColumnType.Boolean:
                return value.TryGetValue<bool>(out var b) ? (b ? "igen" : "nem") : value.ToString();
            case ColumnType.String:
                return ProtectText(value.TryGetValue<string>(out var text) ? text : value.ToJsonString());
            default:
                return value.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        }
    }

    private string FormatNumber(JsonElement element)
    {
        string text;
        if (element.TryGetDecimal(out var d))
            text = d.ToString("0.############################", CultureInfo.InvariantCulture); // no trailing zeros, no exponent
        else
            text = element.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        return decimalSeparator == "." ? text : text.Replace(".", decimalSeparator, StringComparison.Ordinal);
    }

    public static Encoding CreateEncoding(string name) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool WritesBom(string name) => name == Configuration.CsvEncodings.Utf8Bom;
}
