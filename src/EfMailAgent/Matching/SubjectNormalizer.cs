using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EfMailAgent.Matching;

/// <summary>
/// Subject normalization, CLAUDE.md section 6. The add-on has a JavaScript twin (addon/src/core); both are verified by
/// /shared/normalization-cases.json. Every character class is spelled out explicitly (no \s, \d) because .NET and
/// JavaScript define those classes differently.
/// </summary>
public static partial class SubjectNormalizer
{
    // Invisible characters removed outright: soft hyphen, zero-width space/joiners, LRM/RLM, word joiner, BOM.
    private const string InvisibleChars = "\u00AD\u200B\u200C\u200D\u200E\u200F\u2060\uFEFF";

    // Every Unicode whitespace character that either .NET or JavaScript treats as whitespace.
    private const string WhitespaceClass =
        "[\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+";

    // Applied after accents were removed and the text was lowercased, so only plain ASCII forms are needed:
    // "TOV:", "Továbbítás:", "VÁ:", "Válasz:", "Re[2]:", "Fwd (3):", "[EXT]", "[Külső]" ...
    private const string PrefixPattern =
        "^(?:(?:re|fw|fwd|tov|tovabbitas|va|valasz|aw|wg) ?(?:\\[[0-9]+\\]|\\([0-9]+\\))? ?:|\\[[^\\[\\]]{1,30}\\]) ?";

    [GeneratedRegex(WhitespaceClass, RegexOptions.ECMAScript)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(PrefixPattern, RegexOptions.ECMAScript)]
    private static partial Regex PrefixRegex();

    /// <summary>Full subject normalization: text normalization, then repeated stripping of reply/forward prefixes.</summary>
    public static string Normalize(string? subject)
    {
        var text = NormalizeText(subject);
        while (true)
        {
            var match = PrefixRegex().Match(text);
            if (!match.Success || match.Length == 0)
                break;
            text = text[match.Length..];
        }
        return text.Trim(' ');
    }

    /// <summary>
    /// Accent removal (NFD + drop combining marks), invariant lowercase, whitespace collapse and trim. Used for keywords,
    /// which must not lose a leading "re:"-like word.
    /// </summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (rune.IsBmp && InvisibleChars.Contains((char)rune.Value))
                continue;
            sb.Append(rune.ToString());
        }

        var lowered = sb.ToString().ToLowerInvariant();
        return WhitespaceRegex().Replace(lowered, " ").Trim(' ');
    }
}
