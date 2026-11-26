using System.IO.Compression;
using System.Text.Json;
using EfMailAgent.Configuration;

namespace EfMailAgent.Addon;

/// <summary>An add-on XPI on disk with the id and version read from its manifest.json (at the zip root).</summary>
public sealed record AddonPackage(string Path, string Id, string Version)
{
    public const string ExpectedId = "efmailagent@energofish.hu";
    public const string BundledFileName = "efmailagent.xpi";

    public static AddonPackage Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("manifest.json")
                ?? throw new AddonException($"Az XPI nem tartalmaz manifest.json fájlt a gyökerében: {path}");
            using var stream = entry.Open();
            using var manifest = JsonDocument.Parse(stream);
            var root = manifest.RootElement;
            var id = root.TryGetProperty("browser_specific_settings", out var bss) && bss.TryGetProperty("gecko", out var gecko)
                && gecko.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                throw new AddonException($"Az XPI manifest.json fájljából hiányzik az azonosító vagy a verzió: {path}");
            return new AddonPackage(System.IO.Path.GetFullPath(path), id, version);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            throw new AddonException($"Az XPI nem olvasható ({path}): {ex.Message}", ex);
        }
    }

    /// <summary>The XPI shipped next to the exe; during development the one built into the repository's dist folder.</summary>
    public static string? FindBundled(IEnumerable<string>? startDirectories = null)
    {
        var next = System.IO.Path.Combine(AppContext.BaseDirectory, BundledFileName);
        if (startDirectories is null && File.Exists(next))
            return next;

        foreach (var start in startDirectories ?? [AppContext.BaseDirectory, Directory.GetCurrentDirectory()])
        {
            if (File.Exists(System.IO.Path.Combine(start, BundledFileName)))
                return System.IO.Path.Combine(start, BundledFileName);
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "dist", BundledFileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>Versioned file name in the data folder; a new version gives a new install_url, so Thunderbird reinstalls it.</summary>
    public string InstalledFileName => $"efmailagent-{Version}.xpi";

    public static string InstalledPath(EffectiveSettings settings, AddonPackage package) =>
        System.IO.Path.Combine(settings.AddonDir, package.InstalledFileName);
}

public sealed class AddonException(string message, Exception? inner = null) : Exception(message, inner);
