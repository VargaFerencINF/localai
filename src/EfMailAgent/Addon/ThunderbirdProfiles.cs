namespace EfMailAgent.Addon;

public sealed record ThunderbirdProfile(string Name, string Path, bool IsDefault);

/// <summary>
/// Reads %APPDATA%\Thunderbird\profiles.ini. When running as a service the service account's APPDATA is not the user's,
/// so the root can be given explicitly (--user-profile-root).
/// </summary>
public static class ThunderbirdProfiles
{
    public static string DefaultRoot =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thunderbird");

    public static IReadOnlyList<ThunderbirdProfile> Read(string root)
    {
        var ini = System.IO.Path.Combine(root, "profiles.ini");
        if (!File.Exists(ini))
            return [];

        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                sections.Add((line[1..^1], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && sections.Count > 0)
                sections[^1].Values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        // Newer Thunderbird versions mark the default profile per installation in [Install...] Default=<path>.
        var installDefaults = sections.Where(s => s.Name.StartsWith("Install", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Values.GetValueOrDefault("Default")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var profiles = new List<ThunderbirdProfile>();
        foreach (var (name, values) in sections.Where(s => s.Name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)))
        {
            if (!values.TryGetValue("Path", out var path))
                continue;
            var relative = values.GetValueOrDefault("IsRelative", "1") == "1";
            var full = relative ? System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path.Replace('/', System.IO.Path.DirectorySeparatorChar))) : path;
            var isDefault = installDefaults.Count > 0 ? installDefaults.Contains(path) : values.GetValueOrDefault("Default") == "1";
            profiles.Add(new ThunderbirdProfile(values.GetValueOrDefault("Name", name), full, isDefault));
        }
        return profiles;
    }

    public static string ExtensionPath(string profilePath) =>
        System.IO.Path.Combine(profilePath, "extensions", AddonPackage.ExpectedId + ".xpi");
}
