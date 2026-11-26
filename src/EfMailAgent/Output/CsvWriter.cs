using System.Globalization;
using System.Text;
using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using EfMailAgent.Processing;
using EfMailAgent.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfMailAgent.Output;

public sealed record CsvWriteResult(string FilePath, int RowCount, bool IsPending, bool CreatedFile);

public sealed class CsvWriteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Appends result rows to the rule's CSV file (CLAUDE.md section 8): UTF-8 (BOM by default), ';', CRLF, RFC 4180
/// quoting, meta columns first, header on creation. The file is opened exclusively; while it is locked (e.g. open in
/// Excel) writing is retried, finally rows go to &lt;name&gt;_pending_&lt;jobId&gt;.csv. A changed column list never
/// corrupts an existing file: a new &lt;name&gt;_v2.csv (v3, ...) is started instead.
/// </summary>
public sealed class CsvWriter
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly CsvSettings _settings;
    private readonly string _outputDir;
    private readonly TimeZoneInfo _timeZone;
    private readonly ILogger _logger;
    private readonly CsvFormatter _formatter;
    private readonly TimeSpan _lockRetry;
    private readonly TimeSpan _lockRetryInterval;

    public CsvWriter(CsvSettings settings, string outputDir, TimeZoneInfo timeZone, ILogger<CsvWriter>? logger = null,
        TimeSpan? lockRetry = null, TimeSpan? lockRetryInterval = null)
    {
        _settings = settings;
        _outputDir = outputDir;
        _timeZone = timeZone;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _formatter = new CsvFormatter(settings.Delimiter[0], settings.DecimalSeparator);
        _lockRetry = lockRetry ?? TimeSpan.FromSeconds(settings.LockRetrySeconds);
        _lockRetryInterval = lockRetryInterval ?? TimeSpan.FromSeconds(settings.LockRetryIntervalSeconds);
    }

    public static IReadOnlyList<string> HeaderColumns(RuleOutput output) =>
        [.. RuleLoader.MetaColumns, .. output.Columns.Select(c => c.Name)];

    public async Task<CsvWriteResult?> WriteAsync(Rule rule, Job job, IReadOnlyList<ExtractedRow> rows, DateTimeOffset processedAt,
        string? originalSender, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return null;

        Directory.CreateDirectory(_outputDir);
        var header = _formatter.FormatLine(HeaderColumns(rule.Output));
        var body = new StringBuilder();
        foreach (var row in rows)
            body.Append(_formatter.FormatLine(BuildCells(rule.Output, job, row, processedAt, originalSender)));

        var fileName = ResolveFileName(rule, job, processedAt);
        var started = DateTimeOffset.UtcNow;

        while (true)
        {
            try
            {
                var result = rule.Output.Mode == OutputMode.PerJob
                    ? WritePerJob(fileName, header, body.ToString(), rows.Count)
                    : WriteAppend(fileName, header, body.ToString(), rows.Count);
                return result;
            }
            catch (IOException ex) when (IsLockViolation(ex))
            {
                if (DateTimeOffset.UtcNow - started >= _lockRetry)
                {
                    var pending = WritePending(fileName, job.Id, header, body.ToString(), rows.Count);
                    _logger.LogWarning(
                        "A CSV fájl {Elapsed:0} másodperce zárolva van (pl. meg van nyitva Excelben): {File}. A sorok ide kerültek: {Pending}",
                        _lockRetry.TotalSeconds, Path.Combine(_outputDir, fileName), pending.FilePath);
                    return pending;
                }

                _logger.LogInformation("A CSV fájl zárolva van, újrapróbálás {Interval} másodperc múlva: {File}",
                    _lockRetryInterval.TotalSeconds, fileName);
                await Task.Delay(_lockRetryInterval, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CsvWriteException($"A CSV fájl nem írható ({Path.Combine(_outputDir, fileName)}): {ex.Message}", ex);
            }
        }
    }

    public string ResolveFileName(Rule rule, Job job, DateTimeOffset processedAt)
    {
        var local = TimeZoneInfo.ConvertTime(processedAt, _timeZone);
        return rule.Output.FilePattern
            .Replace("{yyyy}", local.ToString("yyyy", CultureInfo.InvariantCulture))
            .Replace("{MM}", local.ToString("MM", CultureInfo.InvariantCulture))
            .Replace("{dd}", local.ToString("dd", CultureInfo.InvariantCulture))
            .Replace("{ruleId}", rule.Id)
            .Replace("{jobId}", job.Id);
    }

    private IEnumerable<string> BuildCells(RuleOutput output, Job job, ExtractedRow row, DateTimeOffset processedAt, string? originalSender)
    {
        yield return job.Id;
        yield return FormatLocal(processedAt);
        yield return job.ReceivedAt is { } received ? FormatLocal(received) : "";
        yield return job.Sender ?? "";
        yield return originalSender ?? "";
        yield return job.Subject ?? "";
        yield return job.MessageId;
        yield return row.Status;
        foreach (var column in output.Columns)
            yield return _formatter.FormatValue(row.Values[column.Name], column.Type);
    }

    private string FormatLocal(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _timeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private CsvWriteResult WriteAppend(string fileName, string header, string body, int rowCount)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var version = 1; ; version++)
        {
            var candidate = Path.Combine(_outputDir, version == 1 ? fileName : $"{baseName}_v{version}{extension}");
            using var stream = new FileStream(candidate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var created = stream.Length == 0;
            if (!created)
            {
                var existingHeader = ReadFirstLine(stream);
                if (existingHeader != header.TrimEnd('\r', '\n'))
                {
                    _logger.LogWarning(
                        "A(z) {File} fejléce eltér a szabály jelenlegi oszlopaitól, ezért új fájlba írok (_v{Next}).",
                        Path.GetFileName(candidate), version + 1);
                    continue;
                }
            }

            WriteBlock(stream, created, header, body);
            return new CsvWriteResult(candidate, rowCount, false, created);
        }
    }

    private CsvWriteResult WritePerJob(string fileName, string header, string body, int rowCount)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(_outputDir, n == 1 ? fileName : $"{baseName}_{n}{extension}");
            try
            {
                using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                WriteBlock(stream, true, header, body);
                return new CsvWriteResult(candidate, rowCount, false, true);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // per_job never appends to an existing file; try the next free name.
            }
        }
    }

    private CsvWriteResult WritePending(string fileName, string jobId, string header, string body, int rowCount)
    {
        var path = Path.Combine(_outputDir, $"{Path.GetFileNameWithoutExtension(fileName)}_pending_{jobId}{Path.GetExtension(fileName)}");
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteBlock(stream, true, header, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CsvWriteException($"A CSV fájl zárolva volt, és a tartalék fájl sem írható ({path}): {ex.Message}", ex);
        }
        return new CsvWriteResult(path, rowCount, true, true);
    }

    private void WriteBlock(FileStream stream, bool newFile, string header, string body)
    {
        var encoding = CsvFormatter.CreateEncoding(_settings.Encoding);
        var bytes = new List<byte>();
        if (newFile)
        {
            if (CsvFormatter.WritesBom(_settings.Encoding))
                bytes.AddRange(Bom);
            bytes.AddRange(encoding.GetBytes(header));
        }
        else
        {
            // Make sure we start on a new line even if someone saved the file without a final line break.
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
                bytes.AddRange("\r\n"u8.ToArray());
        }

        bytes.AddRange(encoding.GetBytes(body));
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes.ToArray());
        stream.Flush(flushToDisk: true);
    }

    private static string ReadFirstLine(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new List<byte>();
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n' && buffer.Count < 1024 * 1024)
            buffer.Add((byte)b);
        var bytes = buffer.ToArray().AsSpan();
        if (bytes.StartsWith(Bom))
            bytes = bytes[3..];
        return Encoding.UTF8.GetString(bytes).TrimEnd('\r');
    }

    /// <summary>Sharing/lock violations (Windows: HRESULT 0x80070020 / 0x80070021; Unix: the flock-based emulation).</summary>
    private static bool IsLockViolation(IOException ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
            return false;
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33 || !OperatingSystem.IsWindows();
    }
}
