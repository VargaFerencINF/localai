using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace EfMailAgent.Configuration;

public sealed class SettingsLoadException(IReadOnlyList<string> errors)
    : Exception("Hibás beállítások: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>What happened during first-run initialization; logged by the caller once logging is up.</summary>
public sealed record BootstrapReport(bool SettingsCreated, bool TokenGenerated, string? SamplesCopiedFrom);

/// <summary>
/// Creates the data folder structure and a default appsettings.json on first run, then loads and validates it.
/// An existing appsettings.json is never overwritten, except that an empty token is filled in.
/// </summary>
public static class SettingsBootstrapper
{
    public const string SettingsFileName = "appsettings.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static (EffectiveSettings Settings, BootstrapReport Report) Initialize(
        DataDirectory dataDirectory, string? samplesRoot = null, bool copySampleRules = true)
    {
        Directory.CreateDirectory(dataDirectory.Path);
        var settingsFile = Path.Combine(dataDirectory.Path, SettingsFileName);

        var created = false;
        var tokenGenerated = false;
        if (!File.Exists(settingsFile))
        {
            var defaults = CreateDefaults(dataDirectory, samplesRoot);
            WriteAtomic(settingsFile, JsonSerializer.Serialize(defaults, WriteOptions));
            created = true;
            tokenGenerated = true;
        }
        else
        {
            tokenGenerated = EnsureToken(settingsFile);
        }

        var settings = Load(settingsFile);
        var effective = new EffectiveSettings(dataDirectory, settings);

        foreach (var dir in new[] { effective.RulesDir, effective.OutputDir, effective.DataDir, effective.LogsDir, effective.AddonDir })
            Directory.CreateDirectory(dir);

        string? copiedFrom = null;
        if (copySampleRules)
            copiedFrom = CopySampleRulesIfEmpty(effective.RulesDir, samplesRoot);

        return (effective, new BootstrapReport(created, tokenGenerated, copiedFrom));
    }

    public static AppSettings Load(string settingsFile)
    {
        IConfigurationRoot config;
        try
        {
            config = new ConfigurationBuilder()
                .AddJsonFile(settingsFile, optional: false, reloadOnChange: false)
                .Build();
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException)
        {
            throw new SettingsLoadException([$"Az {SettingsFileName} nem érvényes JSON: {ex.Message}"]);
        }

        var settings = new AppSettings();
        try
        {
            config.Bind(settings);
        }
        catch (InvalidOperationException ex)
        {
            throw new SettingsLoadException([$"Az {SettingsFileName} egy értéke nem megfelelő típusú: {ex.Message}"]);
        }

        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0)
            throw new SettingsLoadException(errors);
        return settings;
    }

    public static AppSettings CreateDefaults(DataDirectory dataDirectory, string? samplesRoot)
    {
        var settings = new AppSettings();
        settings.Server.Token = TokenGenerator.NewToken();

        // Development data folders use the mock LLM (docs/DEV_MODE.md); production stays on Ollama.
        if (dataDirectory.IsDevelopment)
        {
            var mockDir = samplesRoot is not null
                ? Path.Combine(samplesRoot, "mock-responses")
                : SampleLocator.FindSamplesSubfolder("mock-responses");
            if (mockDir is not null && Directory.Exists(mockDir))
            {
                settings.Llm.Provider = LlmProviders.Mock;
                settings.Llm.MockDir = Path.GetFullPath(mockDir);
            }
        }
        return settings;
    }

    /// <summary>The installer may write a settings file without a token; generate one and keep everything else.</summary>
    private static bool EnsureToken(string settingsFile)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(settingsFile),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return false; // Load() reports the error in Hungarian.
        }

        if (root is not JsonObject obj)
            return false;

        var server = obj["Server"] as JsonObject;
        var token = server?["Token"]?.GetValueKind() == JsonValueKind.String ? server["Token"]!.GetValue<string>() : null;
        if (!string.IsNullOrWhiteSpace(token))
            return false;

        if (server is null)
        {
            server = new JsonObject();
            obj["Server"] = server;
        }
        server["Token"] = TokenGenerator.NewToken();
        WriteAtomic(settingsFile, obj.ToJsonString(WriteOptions));
        return true;
    }

    private static string? CopySampleRulesIfEmpty(string rulesDir, string? samplesRoot)
    {
        if (Directory.EnumerateFileSystemEntries(rulesDir).Any())
            return null;

        var source = samplesRoot is not null ? Path.Combine(samplesRoot, "rules") : SampleLocator.FindSamplesSubfolder("rules");
        if (source is null || !Directory.Exists(source))
            return null;

        CopyDirectory(source, rulesDir);
        return Path.GetFullPath(source);
    }

    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    /// <summary>UTF-8 without BOM, temp file + rename so a crash never leaves a half-written settings file.</summary>
    internal static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
