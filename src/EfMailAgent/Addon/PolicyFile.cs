using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EfMailAgent.Addon;

/// <summary>Our entries in Thunderbird's distribution\policies.json.</summary>
public sealed record AddonPolicy(string ExtensionId, string InstallUrl, string ServerUrl, string Token);

public enum PolicyFileState
{
    Missing,
    Empty,
    Valid,
    Malformed,
}

public sealed record PolicyFileContent(PolicyFileState State, JsonObject? Root, string? Error);

/// <summary>
/// Reads, merges and writes policies.json without ever losing unrelated content: other policies, other extensions'
/// settings and other 3rdparty entries are preserved; a malformed file is reported and never overwritten.
/// </summary>
public static class PolicyFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PolicyPath(string thunderbirdDir) => Path.Combine(thunderbirdDir, "distribution", "policies.json");

    public static PolicyFileContent Load(string path)
    {
        if (!File.Exists(path))
            return new PolicyFileContent(PolicyFileState.Missing, null, null);

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new PolicyFileContent(PolicyFileState.Empty, null, null);

        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject root)
                return new PolicyFileContent(PolicyFileState.Malformed, null, "a fájl gyökere nem JSON objektum");
            if (root["policies"] is { } policies && policies is not JsonObject)
                return new PolicyFileContent(PolicyFileState.Malformed, null, "a \"policies\" kulcs értéke nem objektum");
            return new PolicyFileContent(PolicyFileState.Valid, root, null);
        }
        catch (JsonException ex)
        {
            return new PolicyFileContent(PolicyFileState.Malformed, null, ex.Message);
        }
    }

    /// <summary>Adds or replaces our ExtensionSettings and 3rdparty entries. Returns the new document (input is not modified).</summary>
    public static JsonObject Merge(JsonObject? existing, AddonPolicy policy)
    {
        var root = (JsonObject?)existing?.DeepClone() ?? new JsonObject();
        var policies = GetOrCreate(root, "policies");

        var extensionSettings = GetOrCreate(policies, "ExtensionSettings");
        extensionSettings[policy.ExtensionId] = new JsonObject
        {
            ["installation_mode"] = "force_installed",
            ["install_url"] = policy.InstallUrl,
        };

        var extensions = GetOrCreate(GetOrCreate(policies, "3rdparty"), "Extensions");
        extensions[policy.ExtensionId] = new JsonObject
        {
            ["serverUrl"] = policy.ServerUrl,
            ["token"] = policy.Token,
        };
        return root;
    }

    /// <summary>Removes only our entries; containers that become empty are removed too, everything else stays.</summary>
    public static JsonObject Remove(JsonObject existing, string extensionId)
    {
        var root = (JsonObject)existing.DeepClone();
        if (root["policies"] is not JsonObject policies)
            return root;

        if (policies["ExtensionSettings"] is JsonObject settings)
        {
            settings.Remove(extensionId);
            if (settings.Count == 0)
                policies.Remove("ExtensionSettings");
        }

        if (policies["3rdparty"] is JsonObject thirdParty)
        {
            if (thirdParty["Extensions"] is JsonObject extensions)
            {
                extensions.Remove(extensionId);
                if (extensions.Count == 0)
                    thirdParty.Remove("Extensions");
            }
            if (thirdParty.Count == 0)
                policies.Remove("3rdparty");
        }
        return root;
    }

    /// <summary>Our current entries, if any.</summary>
    public static AddonPolicy? Read(JsonObject? root, string extensionId)
    {
        var policies = root?["policies"] as JsonObject;
        var settings = policies?["ExtensionSettings"]?[extensionId] as JsonObject;
        var managed = policies?["3rdparty"]?["Extensions"]?[extensionId] as JsonObject;
        if (settings is null && managed is null)
            return null;
        return new AddonPolicy(extensionId, Str(settings?["install_url"]), Str(managed?["serverUrl"]), Str(managed?["token"]));
    }

    public static string? InstallationMode(JsonObject? root, string extensionId) =>
        Str(root?["policies"]?["ExtensionSettings"]?[extensionId]?["installation_mode"]) is { Length: > 0 } mode ? mode : null;

    public static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions) + Environment.NewLine;

    /// <summary>Backup of the current file next to it (policies.json.bak-yyyyMMdd-HHmmss); returns its path or null if there was no file.</summary>
    public static string? Backup(string path, DateTimeOffset now)
    {
        if (!File.Exists(path))
            return null;
        var backup = $"{path}.bak-{now:yyyyMMdd-HHmmss}";
        for (var n = 2; File.Exists(backup); n++)
            backup = $"{path}.bak-{now:yyyyMMdd-HHmmss}-{n}";
        File.Copy(path, backup);
        return backup;
    }

    /// <summary>UTF-8 without BOM, pretty printed, temp file + rename; creates the distribution folder if needed.</summary>
    public static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(root), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static JsonObject GetOrCreate(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[key] = created; // a non-object value under one of our keys is replaced; Load() rejects a non-object "policies"
        return created;
    }

    private static string Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
}
