using System.Diagnostics;

namespace EfMailAgent.Cli;

/// <summary>
/// Opens the admin UI as a desktop application window: Microsoft Edge (or Chrome) in app mode, without tabs and
/// address bar, with its own profile so it has its own taskbar entry and window size. Falls back to the default browser.
/// </summary>
public static class AppWindow
{
    public static string? FindBrowser(Func<string, bool>? fileExists = null, Func<string, string?>? environment = null)
    {
        fileExists ??= File.Exists;
        environment ??= Environment.GetEnvironmentVariable;
        if (!OperatingSystem.IsWindows() && fileExists == File.Exists)
            return null; // development on Linux: default browser

        var roots = new[] { environment("ProgramFiles(x86)"), environment("ProgramFiles"), environment("LOCALAPPDATA") }
            .Where(r => !string.IsNullOrEmpty(r)).Cast<string>().ToArray();
        string[] relative =
        [
            @"Microsoft\Edge\Application\msedge.exe",
            @"Google\Chrome\Application\chrome.exe",
        ];
        foreach (var rel in relative)
        {
            foreach (var root in roots)
            {
                var candidate = Path.Combine(root, rel);
                if (fileExists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    public static IReadOnlyList<string> Arguments(string url, string profileDir) =>
    [
        $"--app={url}",
        $"--user-data-dir={profileDir}",
        "--window-size=1380,900",
        "--no-first-run",
        "--no-default-browser-check",
    ];

    public static string ProfileDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EfMailAgent", "app-window");

    /// <summary>Opens the URL in an app window; null on success, otherwise the reason.</summary>
    public static string? TryOpen(string url)
    {
        var browser = FindBrowser();
        if (browser is null)
            return "nincs Microsoft Edge vagy Google Chrome";
        try
        {
            var start = new ProcessStartInfo(browser) { UseShellExecute = false };
            foreach (var argument in Arguments(url, ProfileDir()))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            return process is null ? "a böngésző nem indult el" : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ex.Message.Replace(url, "<bejelentkező link>", StringComparison.Ordinal);
        }
    }
}
