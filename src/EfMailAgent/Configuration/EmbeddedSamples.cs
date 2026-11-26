using System.Reflection;

namespace EfMailAgent.Configuration;

/// <summary>
/// The sample rules and emails compiled into the exe (resources named samples/&lt;folder&gt;/&lt;path&gt;). Used when no
/// samples folder exists on disk, i.e. when EfMailAgent.exe is run on its own without the installer.
/// </summary>
public static class EmbeddedSamples
{
    private const string Prefix = "samples/";
    private static readonly Assembly Assembly = typeof(EmbeddedSamples).Assembly;

    /// <summary>Relative paths (with '/') of the embedded files under one samples subfolder, e.g. "rules".</summary>
    public static IReadOnlyList<string> List(string subfolder)
    {
        var prefix = $"{Prefix}{subfolder}/";
        return Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Writes the embedded files of a subfolder into <paramref name="targetDir"/>; existing files are kept.</summary>
    /// <returns>The number of files written.</returns>
    public static int ExtractTo(string subfolder, string targetDir)
    {
        var written = 0;
        var names = Assembly.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);
        foreach (var relative in List(subfolder))
        {
            var destination = Path.GetFullPath(Path.Combine(targetDir, relative));
            if (!destination.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = Assembly.GetManifestResourceStream(names[$"{Prefix}{subfolder}/{relative}"])!;
            using var target = File.Create(destination);
            source.CopyTo(target);
            written++;
        }
        return written;
    }

    /// <summary>
    /// A folder on disk with the embedded files of a subfolder (extracted once per version into the temp folder), for
    /// code that reads samples from a directory. Null if nothing is embedded.
    /// </summary>
    public static string? ExtractedFolder(string subfolder)
    {
        if (List(subfolder).Count == 0)
            return null;
        var dir = Path.Combine(Path.GetTempPath(), $"EfMailAgent-samples-{VersionInfo.Current}", subfolder);
        try
        {
            ExtractTo(subfolder, dir);
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
