using System.Text;

namespace EfMailAgent.Processing;

/// <summary>
/// Cleans email text before it goes into the prompt: removes zero-width and bidirectional control characters (used to
/// hide or reorder instructions) and other control characters, keeps line breaks and tabs, normalizes CR/LF.
/// The stored job keeps the original text.
/// </summary>
public static class TextSanitizer
{
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (c is '\n' or '\t')
                sb.Append(c);
            else if (IsRemoved(c))
                continue;
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Single-line metadata (subject, sender): additionally line breaks become spaces.</summary>
    public static string CleanLine(string? text) => Clean(text).Replace('\n', ' ').Replace('\t', ' ').Trim();

    public static bool IsRemoved(char c) =>
        char.IsControl(c) // C0, DEL, C1 (tab and line feed are handled by the caller)
        || c is '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' // zero-width space/joiners, LRM, RLM
        || c is >= '\u202A' and <= '\u202E' // LRE, RLE, PDF, LRO, RLO
        || c is >= '\u2060' and <= '\u2064' // word joiner, invisible operators
        || c is >= '\u2066' and <= '\u2069' // LRI, RLI, FSI, PDI
        || c is '\uFEFF' or '\u00AD' or '\u061C' or '\u180E';
}
