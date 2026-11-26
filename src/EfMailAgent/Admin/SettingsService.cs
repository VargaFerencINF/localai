using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Api;
using EfMailAgent.Configuration;

namespace EfMailAgent.Admin;

public sealed record LlmSettingsDto(string Provider, string BaseUrl, string Model, int TimeoutSeconds, double Temperature, int NumCtx,
    bool Think, string MockDir, int MockDelayMs);

public sealed record CsvSettingsDto(string Delimiter, string DecimalSeparator, string Encoding, int LockRetrySeconds);

public sealed record SettingsDto(LlmSettingsDto Llm, CsvSettingsDto Csv, string OutputFolder, string OutputFolderResolved, int Port,
    string TokenPreview, string TimeZone, string SettingsFile, string DataDir);

public sealed record SettingsUpdate(LlmSettingsDto Llm, CsvSettingsDto Csv, string OutputFolder, int Port);

public sealed record SettingsSaveResult(bool Saved, IReadOnlyList<string> Errors, IReadOnlyList<string> AppliedNow, IReadOnlyList<string> RestartRequired);

/// <summary>
/// Reads and rewrites appsettings.json for the Beállítások page. LLM, CSV and output folder changes are applied to the
/// running instance at once (the LLM client is recreated, CSV settings are read per write); the port needs a restart.
/// </summary>
public sealed class SettingsService(EffectiveSettings settings, LlmHealthCache healthCache)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _lock = new();

    public SettingsDto Get()
    {
        var s = settings.Settings;
        var token = s.Server.Token;
        return new SettingsDto(
            new LlmSettingsDto(s.Llm.Provider, s.Llm.BaseUrl, s.Llm.Model, s.Llm.TimeoutSeconds, s.Llm.Temperature, s.Llm.NumCtx, s.Llm.Think,
                s.Llm.MockDir, s.Llm.MockDelayMs),
            new CsvSettingsDto(s.Csv.Delimiter, s.Csv.DecimalSeparator, s.Csv.Encoding, s.Csv.LockRetrySeconds),
            s.Paths.Output, settings.OutputDir, s.Server.Port,
            token.Length > 8 ? $"{token[..4]}…{token[^4..]}" : "****", s.App.TimeZone, settings.SettingsFile, settings.DataDirectory.Path);
    }

    public SettingsSaveResult Save(SettingsUpdate update)
    {
        lock (_lock)
        {
            // Validate a candidate copy with the same rules as at startup.
            var candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings.Settings))!;
            Apply(candidate, update);
            var errors = SettingsValidator.Validate(candidate).ToList();
            var outputDir = new EffectiveSettings(settings.DataDirectory, candidate).OutputDir;
            if (errors.Count == 0 && CheckWritable(outputDir) is { } writeError)
                errors.Add(writeError);
            if (errors.Count > 0)
                return new SettingsSaveResult(false, errors, [], []);

            var current = settings.Settings;
            var applied = new List<string>();
            var restart = new List<string>();
            if (current.Llm.Provider != candidate.Llm.Provider || current.Llm.BaseUrl != candidate.Llm.BaseUrl || current.Llm.Model != candidate.Llm.Model
                || current.Llm.TimeoutSeconds != candidate.Llm.TimeoutSeconds || current.Llm.Temperature != candidate.Llm.Temperature
                || current.Llm.NumCtx != candidate.Llm.NumCtx || current.Llm.Think != candidate.Llm.Think || current.Llm.MockDir != candidate.Llm.MockDir
                || current.Llm.MockDelayMs != candidate.Llm.MockDelayMs)
                applied.Add("AI (LLM) beállítások");
            if (current.Csv.Delimiter != candidate.Csv.Delimiter || current.Csv.DecimalSeparator != candidate.Csv.DecimalSeparator
                || current.Csv.Encoding != candidate.Csv.Encoding || current.Csv.LockRetrySeconds != candidate.Csv.LockRetrySeconds)
                applied.Add("CSV beállítások");
            if (current.Paths.Output != candidate.Paths.Output)
                applied.Add("Kimeneti mappa");
            if (current.Server.Port != candidate.Server.Port)
                restart.Add("Port (a szolgáltatás újraindítása és az install-addon újrafuttatása kell, hogy az add-on az új portot kapja)");

            WriteFile(update);

            // Live apply: the same AppSettings instance is used everywhere.
            var port = current.Server.Port;
            Apply(current, update);
            current.Server.Port = port; // only after restart
            healthCache.Invalidate();
            return new SettingsSaveResult(true, [], applied, restart);
        }
    }

    private static void Apply(AppSettings target, SettingsUpdate update)
    {
        target.Llm.Provider = update.Llm.Provider.Trim();
        target.Llm.BaseUrl = update.Llm.BaseUrl.Trim();
        target.Llm.Model = update.Llm.Model.Trim();
        target.Llm.TimeoutSeconds = update.Llm.TimeoutSeconds;
        target.Llm.Temperature = update.Llm.Temperature;
        target.Llm.NumCtx = update.Llm.NumCtx;
        target.Llm.Think = update.Llm.Think;
        target.Llm.MockDir = update.Llm.MockDir.Trim();
        target.Llm.MockDelayMs = update.Llm.MockDelayMs;
        target.Csv.Delimiter = update.Csv.Delimiter;
        target.Csv.DecimalSeparator = update.Csv.DecimalSeparator;
        target.Csv.Encoding = update.Csv.Encoding;
        target.Csv.LockRetrySeconds = update.Csv.LockRetrySeconds;
        target.Paths.Output = string.IsNullOrWhiteSpace(update.OutputFolder) ? "output" : update.OutputFolder.Trim();
        target.Server.Port = update.Port;
    }

    /// <summary>Rewrites only the edited keys; everything else in the file (token, worker settings, unknown keys) stays.</summary>
    private void WriteFile(SettingsUpdate update)
    {
        var path = settings.SettingsFile;
        var root = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) : null) as JsonObject
                   ?? new JsonObject();
        JsonObject Section(string name) => root[name] as JsonObject ?? (JsonObject)(root[name] = new JsonObject());

        var llm = Section("Llm");
        llm["Provider"] = update.Llm.Provider.Trim();
        llm["BaseUrl"] = update.Llm.BaseUrl.Trim();
        llm["Model"] = update.Llm.Model.Trim();
        llm["TimeoutSeconds"] = update.Llm.TimeoutSeconds;
        llm["Temperature"] = update.Llm.Temperature;
        llm["NumCtx"] = update.Llm.NumCtx;
        llm["Think"] = update.Llm.Think;
        llm["MockDir"] = update.Llm.MockDir.Trim();
        llm["MockDelayMs"] = update.Llm.MockDelayMs;
        var csv = Section("Csv");
        csv["Delimiter"] = update.Csv.Delimiter;
        csv["DecimalSeparator"] = update.Csv.DecimalSeparator;
        csv["Encoding"] = update.Csv.Encoding;
        csv["LockRetrySeconds"] = update.Csv.LockRetrySeconds;
        Section("Paths")["Output"] = string.IsNullOrWhiteSpace(update.OutputFolder) ? "output" : update.OutputFolder.Trim();
        Section("Server")["Port"] = update.Port;

        if (File.Exists(path))
            File.Copy(path, path + ".bak", overwrite: true);
        SettingsBootstrapper.WriteAtomic(path, root.ToJsonString(WriteOptions));
    }

    private static string? CheckWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".efmailagent-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"A kimeneti mappa nem írható: {dir} ({ex.Message})";
        }
    }
}
