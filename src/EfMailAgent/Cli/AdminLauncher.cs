using System.Diagnostics;
using EfMailAgent.Admin;
using EfMailAgent.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace EfMailAgent.Cli;

/// <summary>Opens the admin UI in the default browser, logged in through a one-time link.</summary>
public static class AdminLauncher
{
    public const string NoBrowserVariable = "EFMAILAGENT_NO_BROWSER";

    /// <summary>
    /// True when a person started the program (double click, Start menu, console): not as a Windows service, not with
    /// redirected input (scripts, CI), not with --no-browser or EFMAILAGENT_NO_BROWSER.
    /// </summary>
    public static bool ShouldOpenBrowser(bool noBrowserOption) =>
        !noBrowserOption
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NoBrowserVariable))
        && !WindowsServiceHelpers.IsWindowsService()
        && Environment.UserInteractive
        && !Console.IsInputRedirected;

    public static string LoginUrl(EffectiveSettings settings, string? page = null) =>
        LoginLink.CreateUrl(settings.Settings.Server.Port, settings.Settings.Server.Token, DateTimeOffset.UtcNow, page);

    public static string PlainUrl(EffectiveSettings settings) => $"http://127.0.0.1:{settings.Settings.Server.Port}/admin/";

    /// <summary>Returns null on success, otherwise the reason (Hungarian).</summary>
    public static string? TryOpen(string url)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NoBrowserVariable)))
            return $"a böngésző megnyitása ki van kapcsolva ({NoBrowserVariable})";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // The message contains the link, i.e. a login secret: never let it reach the log or the console.
            return ex.Message.Replace(url, "<bejelentkező link>", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Reads appsettings.json without creating or writing anything (no log file either): a normal user starting the
    /// Start menu shortcut while the service runs may not write the service's files. Null if there are no settings yet.
    /// </summary>
    public static EffectiveSettings? LoadReadOnly(string? dataDirArgument)
    {
        var dataDirectory = DataDirectory.Resolve(dataDirArgument);
        var file = Path.Combine(dataDirectory.Path, SettingsBootstrapper.SettingsFileName);
        if (!File.Exists(file))
            return null;
        try
        {
            return new EffectiveSettings(dataDirectory, SettingsBootstrapper.Load(file));
        }
        catch (Exception ex) when (ex is SettingsLoadException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// If an instance already runs: opens its UI (logged in) and returns true, without starting anything or writing
    /// any file. Used when a person starts the exe a second time (Start menu, double click).
    /// </summary>
    public static async Task<bool> OpenRunningInstanceAsync(string? dataDirArgument, TextWriter output, CancellationToken cancellationToken)
    {
        var settings = LoadReadOnly(dataDirArgument);
        if (settings is null || !await IsRunningAsync(settings, cancellationToken))
            return false;
        var failure = TryOpen(LoginUrl(settings, "attekintes"));
        output.WriteLine(failure is null
            ? $"Az EfMailAgent már fut, a kezelőfelület megnyílt a böngészőben: {PlainUrl(settings)}"
            : $"Az EfMailAgent már fut, de a böngésző nem nyitható meg ({failure}). Kezelőfelület: {PlainUrl(settings)}");
        return true;
    }

    /// <summary>True if an EfMailAgent with this token already answers on the configured port.</summary>
    public static async Task<bool> IsRunningAsync(EffectiveSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings.Settings.Server.Port}/api/health");
            request.Headers.Add("X-EfMailAgent-Token", settings.Settings.Server.Token);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
