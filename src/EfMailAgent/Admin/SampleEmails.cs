using System.Text.Json;
using EfMailAgent.Configuration;
using EfMailAgent.Email;

namespace EfMailAgent.Admin;

public sealed record SampleEmail(string File, string Subject, string From, DateTimeOffset? ReceivedAt, string Body);

/// <summary>Sample emails for the Tesztkonzol (samples\emails next to the exe or in the repository).</summary>
public static class SampleEmails
{
    public static IReadOnlyList<SampleEmail> Load(string? emailsDir = null)
    {
        emailsDir ??= SampleLocator.FindSamplesSubfolder("emails");
        if (emailsDir is null || !Directory.Exists(emailsDir))
            return [];

        var index = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var indexPath = Path.Combine(emailsDir, "index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
                foreach (var email in document.RootElement.GetProperty("emails").EnumerateArray())
                    index[email.GetProperty("file").GetString()!] = email.Clone();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // an index file with an unexpected shape only loses the metadata
            }
        }

        var result = new List<SampleEmail>();
        foreach (var path in Directory.EnumerateFiles(emailsDir).Where(f => f.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Order())
        {
            try
            {
                var email = EmailFile.Load(path);
                var name = Path.GetFileName(path);
                index.TryGetValue(name, out var meta);
                string? Meta(string key) => meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var received = email.ReceivedAt ?? (DateTimeOffset.TryParse(Meta("receivedAt"), out var r) ? r : null);
                result.Add(new SampleEmail(name, Meta("subject") ?? email.Subject, Meta("from") ?? email.From, received, email.BodyText));
            }
            catch (Exception ex) when (ex is IOException or FormatException or MimeKit.ParseException)
            {
                // skip unreadable samples
            }
        }
        return result;
    }
}
