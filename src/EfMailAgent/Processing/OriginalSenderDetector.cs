using System.Text.RegularExpressions;

namespace EfMailAgent.Processing;

/// <summary>
/// Finds the original sender in a forwarded email body by looking for forward / quoted-header blocks written by
/// Outlook, Gmail and Thunderbird (Hungarian and English). In a forward chain the first sender outside the
/// forwarder's own domain wins (that is the customer); if every sender is internal, the deepest one is used.
/// </summary>
public static partial class OriginalSenderDetector
{
    // "Feladó:", "From:", "Von:" header lines, optionally indented or tab separated, optionally bold-marked ("*From:*").
    [GeneratedRegex(@"^[ \t>*]*(?:Feladó|Feladó neve|From|Von)\*?[ \t]*:[ \t]*(?<value>.+?)[ \t*]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FromLineRegex();

    // Other header lines that appear next to the sender line in a quoted header block.
    [GeneratedRegex(@"^[ \t>*]*(?:Elküldve|Küldve|Dátum|Date|Sent|Gesendet|Tárgy|Subject|Betreff|Címzett|To|An|Másolatot kap|Cc)\*?[ \t]*:", RegexOptions.IgnoreCase)]
    private static partial Regex OtherHeaderRegex();

    // Explicit forward / original-message markers.
    [GeneratedRegex(@"^[ \t>]*(?:-{2,}[ \t]*(?:Továbbított üzenet|Továbbított levél|Eredeti üzenet|Original Message|Forwarded message|Weitergeleitete Nachricht|Ursprüngliche Nachricht)[ \t]*-{2,}|_{10,})[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@(?<domain>[A-Za-z0-9.\-]+\.[A-Za-z]{2,})")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\[mailto:(?<address>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex MailtoRegex();

    public static string? Detect(string? body, string? forwarderAddress)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var candidates = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var match = FromLineRegex().Match(lines[i]);
            if (!match.Success || !IsHeaderBlock(lines, i))
                continue;

            var value = Clean(match.Groups["value"].Value);
            if (value.Length > 0)
                candidates.Add(value);
        }

        if (candidates.Count == 0)
            return null;

        var internalDomain = Domain(forwarderAddress);
        if (internalDomain is not null)
        {
            var external = candidates.FirstOrDefault(c => !string.Equals(Domain(c), internalDomain, StringComparison.OrdinalIgnoreCase));
            if (external is not null)
                return external;
        }
        return candidates[^1];
    }

    /// <summary>A sender line counts if a marker precedes it closely or another header line is right next to it.</summary>
    private static bool IsHeaderBlock(string[] lines, int index)
    {
        for (var i = Math.Max(0, index - 3); i < index; i++)
        {
            if (MarkerRegex().IsMatch(lines[i]))
                return true;
        }

        for (var i = Math.Max(0, index - 4); i <= Math.Min(lines.Length - 1, index + 4); i++)
        {
            if (i != index && OtherHeaderRegex().IsMatch(lines[i]))
                return true;
        }
        return false;
    }

    /// <summary>Normalizes "Name [mailto:a@b]" and quoted names to "Name &lt;a@b&gt;".</summary>
    private static string Clean(string value)
    {
        value = value.Trim();
        var mailto = MailtoRegex().Match(value);
        if (mailto.Success)
        {
            var name = value[..mailto.Index].Trim().Trim('"', '\'').Trim();
            var address = mailto.Groups["address"].Value.Trim();
            value = name.Length > 0 ? $"{name} <{address}>" : address;
        }

        var lt = value.IndexOf('<');
        if (lt > 0)
        {
            var name = value[..lt].Trim().Trim('"', '\'').Trim();
            value = $"{name} {value[lt..].Trim()}";
        }
        return value.Trim();
    }

    private static string? Domain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = EmailRegex().Match(text);
        return match.Success ? match.Groups["domain"].Value : null;
    }
}
