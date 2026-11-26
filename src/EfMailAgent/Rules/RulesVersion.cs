using System.Security.Cryptography;
using System.Text;

namespace EfMailAgent.Rules;

/// <summary>Content hash of every file in the rules folder; changes whenever any rule, prompt or skill changes.</summary>
public static class RulesVersion
{
    public static string Compute(string rulesDir)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(rulesDir))
        {
            var files = Directory.EnumerateFiles(rulesDir, "*", SearchOption.AllDirectories)
                .Where(f => IsRelevant(rulesDir, f))
                .Select(f => (Full: f, Relative: Path.GetRelativePath(rulesDir, f).Replace('\\', '/')))
                .OrderBy(f => f.Relative, StringComparer.Ordinal);

            foreach (var (full, relative) in files)
            {
                byte[] content;
                try
                {
                    content = File.ReadAllBytes(full);
                }
                catch (IOException)
                {
                    content = Encoding.UTF8.GetBytes("<unreadable>");
                }
                catch (UnauthorizedAccessException)
                {
                    content = Encoding.UTF8.GetBytes("<unreadable>");
                }

                hash.AppendData(Encoding.UTF8.GetBytes($"{relative}\n{content.Length}\n"));
                hash.AppendData(content);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>Backups (.bak), temp files and hidden files/folders do not affect the version.</summary>
    public static bool IsRelevant(string rulesDir, string fullPath)
    {
        var relative = Path.GetRelativePath(rulesDir, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s.StartsWith('.')))
            return false;
        var name = segments[^1];
        return !(name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                 || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                 || name.EndsWith('~'));
    }
}
