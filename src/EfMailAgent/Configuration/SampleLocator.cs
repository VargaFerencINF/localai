namespace EfMailAgent.Configuration;

/// <summary>
/// Finds the bundled "samples" folder: next to the exe (installed layout) or by walking up from the exe / current
/// directory (repository layout during development).
/// </summary>
public static class SampleLocator
{
    public static string? FindSamplesSubfolder(string relative, IEnumerable<string>? startDirectories = null)
    {
        startDirectories ??= [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];
        foreach (var start in startDirectories)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "samples", relative);
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}
