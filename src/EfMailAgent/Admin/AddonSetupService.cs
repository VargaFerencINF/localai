using System.ComponentModel;
using System.Diagnostics;
using EfMailAgent.Addon;
using EfMailAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Admin;

public sealed record AddonInstallResult(bool Ok, IReadOnlyList<string> Messages);

/// <summary>Runs "EfMailAgent.exe install-addon" with administrator rights (Windows UAC prompt); faked in tests.</summary>
public interface IElevatedRunner
{
    /// <returns>The exit code, or null if the person declined the Windows prompt.</returns>
    Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class WindowsElevatedRunner : IElevatedRunner
{
    public async Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var (exe, prefix) = SelfCommand();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas", // UAC prompt on the desktop of the person using the app
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(' ', prefix.Concat(arguments).Select(Quote)),
        };
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("A folyamat nem indult el.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the prompt was declined
        {
            return null;
        }
    }

    /// <summary>This program: the single-file exe, or "dotnet EfMailAgent.dll" during development.</summary>
    private static (string Exe, string[] Prefix) SelfCommand()
    {
        var process = Environment.ProcessPath ?? "EfMailAgent.exe";
        var name = Path.GetFileNameWithoutExtension(process);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (process, [Path.Combine(AppContext.BaseDirectory, "EfMailAgent.dll")]); // single-file safe (no Assembly.Location)
        return (process, []);
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
}

/// <summary>
/// The "Thunderbird bővítmény" part of the Beállítások page: status and one-click install / repair into the Thunderbird
/// on this machine. With administrator rights (the service, or an elevated app) it installs in-process; otherwise it
/// starts install-addon elevated, which shows the Windows UAC prompt.
/// </summary>
public sealed class AddonSetupService(EffectiveSettings settings, IElevationCheck elevation, IElevatedRunner elevatedRunner,
    ILogger<AddonSetupService> logger, Func<bool>? isWindows = null)
{
    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

    public bool CanInstallDirectly => elevation.IsElevated() || !_isWindows();

    public object Status()
    {
        try
        {
            var status = AddonSelfHealService.CreateInstaller(settings).GetStatus();
            return new
            {
                ok = status.Ok,
                mode = status.State?.Mode,
                bundledVersion = status.Bundled?.Version,
                installedVersion = status.InstalledXpi?.Version,
                thunderbirdRunning = status.ThunderbirdRunning,
                needsElevation = !CanInstallDirectly,
                installations = status.Installations.Select(i => new
                {
                    dir = i.Installation.InstallDir,
                    version = i.Installation.Version,
                    entry = i.Entry is not null,
                    ok = i.Ok,
                    tokenOk = i.TokenOk,
                    urlOk = i.InstallUrlOk,
                }),
                error = status.BundledError,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new { ok = false, error = ex.Message, needsElevation = !CanInstallDirectly, installations = Array.Empty<object>() };
        }
    }

    public async Task<AddonInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Thunderbird bővítmény telepítése a kezelőfelületről ({Mode}).", CanInstallDirectly ? "közvetlenül" : "rendszergazdai jóváhagyással");
        if (CanInstallDirectly)
        {
            var result = AddonSelfHealService.CreateInstaller(settings).Install(new InstallOptions());
            return Describe(result.ExitCode, result.Messages);
        }

        var code = await elevatedRunner.RunElevatedAsync(["install-addon", "--data-dir", settings.DataDirectory.Path], cancellationToken);
        if (code is null)
            return new AddonInstallResult(false, ["A telepítéshez rendszergazdai jóváhagyás kell, de a Windows engedélykérést elutasították. Próbáld újra, és a felugró ablakban válaszd az Igen gombot."]);
        return Describe(code.Value, []);
    }

    private static AddonInstallResult Describe(int code, IReadOnlyList<string> details)
    {
        var messages = new List<string>(details);
        switch (code)
        {
            case AddonExitCodes.Ok:
                messages.Add("A bővítmény telepítve. Indítsd újra a Thunderbirdöt (teljesen zárd be, majd nyisd meg), ezután megjelenik a Kiegészítők között.");
                return new AddonInstallResult(true, messages);
            case AddonExitCodes.NotElevated:
                messages.Add("A telepítéshez rendszergazdai jog kell.");
                break;
            case AddonExitCodes.PolicyFileMalformed:
                messages.Add("A Thunderbird policies.json fájlja hibás, ezért nem módosítottam. Javítsd vagy töröld, majd próbáld újra.");
                break;
            case AddonExitCodes.AddonError when details.Count == 0:
                messages.Add("A Thunderbird nem található ezen a gépen, vagy a bővítmény fájl hiányzik. Telepítsd a Thunderbirdöt, majd próbáld újra.");
                break;
            case AddonExitCodes.AddonError:
                break;
            default:
                messages.Add($"A telepítés nem sikerült (kód: {code}). Részletek a naplóban.");
                break;
        }
        return new AddonInstallResult(false, messages);
    }
}
