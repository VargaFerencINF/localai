using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EfMailAgent.Configuration;

/// <summary>Edits individual keys of appsettings.json (installer, admin UI): other keys stay, the result is validated.</summary>
public static class SettingsEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Applies the edits, validates the result and writes it (keeping appsettings.json.bak). Returns Hungarian errors.</summary>
    public static IReadOnlyList<string> Update(string settingsFile, Action<JsonObject> edit)
    {
        var original = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "{}";
        var root = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? new JsonObject();
        edit(root);
        var text = root.ToJsonString(WriteOptions);

        var probe = settingsFile + ".check";
        File.WriteAllText(probe, text);
        try
        {
            SettingsBootstrapper.Load(probe);
        }
        catch (SettingsLoadException ex)
        {
            return ex.Errors;
        }
        finally
        {
            File.Delete(probe);
        }

        if (File.Exists(settingsFile))
            File.Copy(settingsFile, settingsFile + ".bak", overwrite: true);
        SettingsBootstrapper.WriteAtomic(settingsFile, text);
        return [];
    }

    public static JsonObject Section(JsonObject root, string name) => root[name] as JsonObject ?? (JsonObject)(root[name] = new JsonObject());
}
