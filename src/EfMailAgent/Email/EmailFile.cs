using EfMailAgent.Jobs;
using MimeKit;

namespace EfMailAgent.Email;

/// <summary>An email read from disk (CLI test-rule, samples).</summary>
public sealed record EmailFileContent(
    string? MessageId,
    string Subject,
    string From,
    string To,
    DateTimeOffset? ReceivedAt,
    string BodyText,
    string? BodyHtml,
    IReadOnlyList<string> AttachmentNames);

public static class EmailFile
{
    /// <summary>.eml files are parsed as MIME messages; anything else is read as the plain text body.</summary>
    public static EmailFileContent Load(string path)
    {
        if (!path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            return new EmailFileContent(null, "", "", "", null, File.ReadAllText(path), null, []);

        var message = MimeMessage.Load(path);
        var html = message.HtmlBody;
        var text = message.TextBody ?? (html is null ? "" : HtmlToText.Convert(html));
        var attachments = message.Attachments
            .Select(a => a is MimePart part ? part.FileName : a.ContentDisposition?.FileName)
            .OfType<string>()
            .ToArray();

        return new EmailFileContent(
            string.IsNullOrWhiteSpace(message.MessageId) ? null : MessageIds.Canonicalize(message.MessageId),
            message.Subject ?? "",
            Format(message.From),
            Format(message.To),
            message.Headers.Contains(HeaderId.Date) ? message.Date : null,
            text,
            html,
            attachments);
    }

    /// <summary>"Name &lt;address&gt;" without MIME quoting, comma separated.</summary>
    public static string Format(InternetAddressList addresses) =>
        string.Join(", ", addresses.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));
}
