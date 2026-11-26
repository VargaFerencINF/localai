using System.Text;
using System.Text.RegularExpressions;

namespace EfMailAgent.Admin;

public sealed record LogEntry(string Time, string Level, string Source, string Message);

public sealed record LogTailResult(string? File, IReadOnlyList<LogEntry> Entries);

/// <summary>Tail of the current Serilog file for the Napló page (multi-line entries such as exceptions stay together).</summary>
public static partial class LogTail
{
    public static readonly string[] Levels = ["VRB", "DBG", "INF", "WRN", "ERR", "FTL"];
    private const int MaxBytes = 2 * 1024 * 1024;

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \S+ \[(?<level>VRB|DBG|INF|WRN|ERR|FTL)\] (?<source>[^:]*): (?<message>.*)$")]
    private static partial Regex EntryStart();

    public static LogTailResult Read(string logsDir, string minLevel = "INF", int maxEntries = 300)
    {
        var file = Directory.Exists(logsDir)
            ? new DirectoryInfo(logsDir).GetFiles("efmailagent-*.log").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).FirstOrDefault()
            : null;
        if (file is null)
            return new LogTailResult(null, []);

        string text;
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > MaxBytes)
                stream.Seek(-MaxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }

        var min = Math.Max(0, Array.IndexOf(Levels, minLevel.ToUpperInvariant()));
        var entries = new List<LogEntry>();
        LogEntry? current = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = EntryStart().Match(line);
            if (match.Success)
            {
                if (current is not null)
                    entries.Add(current);
                current = new LogEntry(match.Groups["time"].Value, match.Groups["level"].Value, match.Groups["source"].Value, match.Groups["message"].Value);
            }
            else if (current is not null && line.Length > 0)
            {
                current = current with { Message = current.Message + "\n" + line };
            }
        }
        if (current is not null)
            entries.Add(current);

        var filtered = entries.Where(e => Array.IndexOf(Levels, e.Level) >= min).ToList();
        return new LogTailResult(file.FullName, filtered.Skip(Math.Max(0, filtered.Count - Math.Clamp(maxEntries, 10, 2000))).ToArray());
    }
}
