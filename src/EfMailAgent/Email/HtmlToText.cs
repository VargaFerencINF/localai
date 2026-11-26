using System.Net;
using System.Text.RegularExpressions;

namespace EfMailAgent.Email;

/// <summary>
/// Small, dependency-free HTML to plain text conversion for email bodies: drops scripts/styles/comments, keeps line
/// breaks for block elements, list items as "- ", table cells separated by tabs, decodes entities.
/// </summary>
public static partial class HtmlToText
{
    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DroppedBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnds();

    [GeneratedRegex(@"</?(div|tr|li|ul|ol|table|h[1-6]|blockquote|pre|section|article|header|footer)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"</?p\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphTags();

    // A run of block boundaries (with only spaces between them) becomes one line break, or a blank line for paragraphs.
    [GeneratedRegex("[\u0001\u0002][\u0001\u0002 \t]*")]
    private static partial Regex BoundaryRuns();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewLines();

    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = html.Replace("\r\n", "\n").Replace('\r', '\n');
        text = DroppedBlocks().Replace(text, "");
        text = Comments().Replace(text, "");
        text = text.Replace("\n", " "); // source line breaks are not significant in HTML
        text = LineBreaks().Replace(text, "\n");
        text = ListItems().Replace(text, "\u0001- ");
        text = CellEnds().Replace(text, "\t");
        text = ParagraphTags().Replace(text, "\u0002");
        text = BlockTags().Replace(text, "\u0001");
        text = BoundaryRuns().Replace(text, m =>
        {
            var marker = m.Value.TrimEnd(' ', '\t');
            var trailing = m.Value[marker.Length..];
            return (marker.Contains('\u0002') ? "\n\n" : "\n") + trailing;
        });
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);

        var lines = text.Split('\n').Select(l => HorizontalSpace().Replace(l, " ").Trim());
        text = string.Join("\n", lines);
        return ManyNewLines().Replace(text, "\n\n").Trim();
    }
}
