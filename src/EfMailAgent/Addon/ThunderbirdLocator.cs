using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EfMailAgent.Addon;

public sealed record ThunderbirdInstallation(string InstallDir, string? Version, string Source);

/// <summary>Registry access behind an interface so the locator logic is testable on any OS.</summary>
public interface IRegistryReader
{
    /// <summary>Subkey names of HKLM\<paramref name="path"/> in the 64-bit (<paramref name="wow64"/> false) or 32-bit view.</summary>
    IReadOnlyList<string> GetSubKeyNames(string path, bool wow64);

    string? GetValue(string path, string name, bool wow64);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryReader : IRegistryReader
{
    public IReadOnlyList<string> GetSubKeyNames(string path, bool wow64)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, wow64 ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = root.OpenSubKey(path);
        return key?.GetSubKeyNames() ?? [];
    }

    public string? GetValue(string path, string name, bool wow64)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, wow64 ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }
}

public interface IThunderbirdLocator
{
    IReadOnlyList<ThunderbirdInstallation> FindInstallations();

    bool IsRunning();
}

/// <summary>
/// Finds Thunderbird installations: HKLM\SOFTWARE\Mozilla\Mozilla Thunderbird\&lt;version&gt;\Main\Install Directory
/// (64-bit and 32-bit registry views), then the default Program Files folders. The version comes from application.ini.
/// </summary>
public sealed class ThunderbirdLocator(IRegistryReader? registry, IEnumerable<string>? defaultDirs = null) : IThunderbirdLocator
{
    public const string RegistryPath = @"SOFTWARE\Mozilla\Mozilla Thunderbird";

    public static ThunderbirdLocator CreateDefault()
    {
        IRegistryReader? registry = OperatingSystem.IsWindows() ? new WindowsRegistryReader() : null;
        var dirs = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root))
                dirs.Add(Path.Combine(root, "Mozilla Thunderbird"));
        }
        return new ThunderbirdLocator(registry, dirs);
    }

    public IReadOnlyList<ThunderbirdInstallation> FindInstallations()
    {
        var found = new List<ThunderbirdInstallation>();
        void Add(string? dir, string source)
        {
            if (string.IsNullOrWhiteSpace(dir) || !IsInstallDir(dir))
                return;
            var full = Path.GetFullPath(dir.Trim().Trim('"'));
            if (found.Any(f => string.Equals(f.InstallDir.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                return;
            found.Add(new ThunderbirdInstallation(full, ReadVersion(full), source));
        }

        if (registry is not null)
        {
            foreach (var wow64 in new[] { false, true })
            {
                try
                {
                    foreach (var version in registry.GetSubKeyNames(RegistryPath, wow64))
                        Add(registry.GetValue($@"{RegistryPath}\{version}\Main", "Install Directory", wow64),
                            $"registry ({(wow64 ? "32" : "64")} bit): {version}");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // ignore an unreadable view, fall back to the default paths
                }
            }
        }

        foreach (var dir in defaultDirs ?? [])
            Add(dir, "default path");
        return found;
    }

    public bool IsRunning()
    {
        try
        {
            return Process.GetProcessesByName("thunderbird").Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>An install folder has thunderbird.exe (Windows), thunderbird (Linux) or at least application.ini.</summary>
    public static bool IsInstallDir(string dir)
    {
        dir = dir.Trim().Trim('"');
        return Directory.Exists(dir) && (File.Exists(Path.Combine(dir, "thunderbird.exe")) || File.Exists(Path.Combine(dir, "thunderbird"))
                                         || File.Exists(Path.Combine(dir, "application.ini")));
    }

    /// <summary>[App] Version= from application.ini, e.g. 140.5.0esr.</summary>
    public static string? ReadVersion(string installDir)
    {
        var ini = Path.Combine(installDir, "application.ini");
        if (!File.Exists(ini))
            return null;
        var section = "";
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                section = line[1..^1];
            else if (section.Equals("App", StringComparison.OrdinalIgnoreCase) && line.StartsWith("Version=", StringComparison.OrdinalIgnoreCase))
                return line["Version=".Length..].Trim();
        }
        return null;
    }
}

/// <summary>A fixed installation (the --thunderbird-dir option, tests, CI).</summary>
public sealed class FixedThunderbirdLocator(string installDir) : IThunderbirdLocator
{
    public IReadOnlyList<ThunderbirdInstallation> FindInstallations() =>
        [new(Path.GetFullPath(installDir), ThunderbirdLocator.ReadVersion(installDir), "--thunderbird-dir")];

    public bool IsRunning() => false;
}

/// <summary>Known install folders (e.g. recorded by install-addon) plus whatever the fallback locator finds.</summary>
public sealed class CombinedThunderbirdLocator(IEnumerable<string> knownDirs, IThunderbirdLocator fallback) : IThunderbirdLocator
{
    public IReadOnlyList<ThunderbirdInstallation> FindInstallations()
    {
        var result = knownDirs.Where(ThunderbirdLocator.IsInstallDir)
            .Select(d => new ThunderbirdInstallation(Path.GetFullPath(d), ThunderbirdLocator.ReadVersion(d), "install-state"))
            .ToList();
        foreach (var installation in fallback.FindInstallations())
        {
            if (!result.Any(r => string.Equals(r.InstallDir, installation.InstallDir, StringComparison.OrdinalIgnoreCase)))
                result.Add(installation);
        }
        return result;
    }

    public bool IsRunning() => fallback.IsRunning();
}
