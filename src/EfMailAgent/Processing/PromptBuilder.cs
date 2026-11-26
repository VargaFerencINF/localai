using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EfMailAgent.Llm;
using EfMailAgent.Rules;

namespace EfMailAgent.Processing;

/// <summary>Input of prompt assembly: the email as stored in a job (or read by the CLI).</summary>
public sealed record PromptInput(
    string? Sender,
    string? Subject,
    DateTimeOffset? ReceivedAt,
    string? BodyText,
    IReadOnlyList<string> AttachmentNames);

public sealed record AssembledPrompt(string System, string User, string? OriginalSender, bool BodyTruncated, int BodyLength)
{
    public IReadOnlyList<LlmMessage> Messages => [new(LlmRoles.System, System), new(LlmRoles.User, User)];

    /// <summary>Human-readable form (CLI, mock prompt dump, admin UI).</summary>
    public string ToDisplayString() => $"===== SYSTEM =====\n{System}\n\n===== USER =====\n{User}\n";
}

/// <summary>
/// Prompt assembly, CLAUDE.md section 5 (order matters):
/// 1. _base/system.md, 2. the rule's prompt.md, 3. every skill ("### Skill: name"), 4. generated field list;
/// user message: metadata block + body inside &lt;email&gt;...&lt;/email&gt;, truncated to max_body_chars.
/// </summary>
public static partial class PromptBuilder
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    [GeneratedRegex(@"<\s*/?\s*email\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex EmailTagRegex();

    public static AssembledPrompt Build(Rule rule, string baseSystemPrompt, PromptInput input, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var originalSender = OriginalSenderDetector.Detect(input.BodyText, input.Sender);
        var today = TimeZoneInfo.ConvertTime(now, timeZone).ToString(DateFormat, CultureInfo.InvariantCulture);
        var receivedAt = input.ReceivedAt is { } r
            ? TimeZoneInfo.ConvertTime(r, timeZone).ToString(DateTimeFormat, CultureInfo.InvariantCulture)
            : "";

        var placeholders = new Dictionary<string, string>
        {
            ["today"] = today,
            ["sender"] = input.Sender ?? "",
            ["original_sender"] = originalSender ?? "",
            ["subject"] = input.Subject ?? "",
            ["received_at"] = receivedAt,
            ["rule_name"] = rule.Name,
        };

        var system = new StringBuilder();
        AppendSection(system, Replace(baseSystemPrompt, placeholders));
        AppendSection(system, Replace(rule.PromptText, placeholders));
        foreach (var skill in rule.Skills)
            AppendSection(system, $"### Skill: {skill.FileName}\n\n{Replace(skill.Content, placeholders).Trim()}");
        AppendSection(system, BuildFieldList(rule.Output));

        var body = (input.BodyText ?? "").Replace("\r\n", "\n");
        var bodyLength = body.Length;
        var truncated = bodyLength > rule.Llm.MaxBodyChars;
        if (truncated)
            body = body[..rule.Llm.MaxBodyChars];
        // The body must not be able to close the <email> block (prompt injection).
        body = EmailTagRegex().Replace(body, m => m.Value.Replace("<", "‹").Replace(">", "›"));

        var user = new StringBuilder();
        user.Append("Metaadatok:\n");
        user.Append($"- Feladó (aki nekünk küldte/továbbította): {Value(input.Sender)}\n");
        user.Append($"- Eredeti feladó (a továbbított részből): {Value(originalSender, "nem azonosítható")}\n");
        user.Append($"- Tárgy: {Value(input.Subject)}\n");
        user.Append($"- Beérkezés ideje: {Value(receivedAt)}\n");
        user.Append($"- Mai dátum: {today}\n");
        if (input.AttachmentNames.Count > 0)
            user.Append($"- Mellékletek (csak a nevük, a tartalmuk nem érhető el): {string.Join(", ", input.AttachmentNames)}\n");
        user.Append('\n');
        user.Append("<email>\n");
        user.Append(body.TrimEnd());
        user.Append("\n</email>");
        if (truncated)
            user.Append($"\n\nMegjegyzés: az e-mail törzse túl hosszú volt ({bodyLength} karakter), ezért az első {rule.Llm.MaxBodyChars} karakter után le lett vágva.");

        return new AssembledPrompt(system.ToString().TrimEnd(), user.ToString(), originalSender, truncated, bodyLength);
    }

    /// <summary>Step 4 of the prompt: the columns in plain Hungarian, derived from rule.yaml.</summary>
    public static string BuildFieldList(RuleOutput output)
    {
        var sb = new StringBuilder();
        sb.Append("## A kinyerendő mezők\n\n");
        sb.Append($"A válasz egy JSON objektum, amelynek \"{output.RowsPath}\" tömbjében minden elem egy sor, pontosan a következő mezőkkel:\n\n");
        foreach (var column in output.Columns)
        {
            var type = column.Type switch
            {
                ColumnType.String => "szöveg",
                ColumnType.Number => "szám",
                ColumnType.Integer => "egész szám",
                ColumnType.Date => "dátum, ÉÉÉÉ-HH-NN formában",
                ColumnType.Boolean => "logikai érték (true/false)",
                ColumnType.Enum => "felsorolás",
                _ => "szöveg",
            };
            var required = column.Required ? "kötelező" : "nem kötelező, ha nincs adat: null";
            sb.Append($"- {column.Name} ({type}, {required})");
            if (column.Description.Length > 0)
                sb.Append($": {column.Description}");
            if (column.Type == ColumnType.Enum)
                sb.Append($" Lehetséges értékek: {string.Join(", ", column.Values)}.");
            sb.Append('\n');
        }
        sb.Append($"\nHa az e-mailben nincs kinyerhető tétel, a \"{output.RowsPath}\" tömb legyen üres.");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        if (sb.Length > 0)
            sb.Append("\n\n");
        sb.Append(text);
    }

    private static string Replace(string text, IReadOnlyDictionary<string, string> placeholders)
    {
        foreach (var (key, value) in placeholders)
            text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        return text.Replace("\r\n", "\n");
    }

    private static string Value(string? value, string empty = "nincs megadva") =>
        string.IsNullOrWhiteSpace(value) ? empty : value.Trim();
}
