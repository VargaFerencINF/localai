using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace EfMailAgent.Service;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs external tools (sc.exe, icacls.exe); faked in tests.</summary>
public interface IProcessRunner
{
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Nem indítható: {fileName}");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.Trim());
    }
}

/// <summary>Service state access behind an interface (ServiceController is Windows-only).</summary>
public interface IServiceControl
{
    bool Exists(string name);
    string Status(string name);
    void Start(string name, TimeSpan timeout);
    void Stop(string name, TimeSpan timeout);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControl : IServiceControl
{
    public bool Exists(string name) => ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));

    public string Status(string name)
    {
        using var controller = new ServiceController(name);
        return controller.Status.ToString();
    }

    public void Start(string name, TimeSpan timeout)
    {
        using var controller = new ServiceController(name);
        if (controller.Status != ServiceControllerStatus.Running)
        {
            if (controller.Status is ServiceControllerStatus.Stopped)
                controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }
    }

    public void Stop(string name, TimeSpan timeout)
    {
        using var controller = new ServiceController(name);
        if (controller.Status != ServiceControllerStatus.Stopped)
        {
            if (controller.CanStop)
                controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
    }
}

public sealed record ServiceInstallOptions(string ExePath, string DataDir, string? Account = null, string? Password = null, bool Start = true);

public sealed record ServiceOperationResult(int ExitCode, IReadOnlyList<string> Messages)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Registers the Windows service "EfMailAgent" with sc.exe: automatic (delayed) start, restart after 1 minute on the
/// first three failures (also for non-crash failures), optional service account with modify rights on the data folder.
/// </summary>
public sealed class ServiceManager(IProcessRunner runner, IServiceControl? control)
{
    public const string ServiceName = "EfMailAgent";
    public const string DisplayName = "EfMailAgent e-mail AI feldolgozó";
    public const string Description = "Továbbított e-mailek (pl. előrendelések) feldolgozása helyi AI-val, strukturált CSV kimenettel. Admin felület: http://127.0.0.1:47811/admin";
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(60);

    public static ServiceManager CreateDefault() =>
        new(new SystemProcessRunner(), OperatingSystem.IsWindows() ? new WindowsServiceControl() : null);

    public ServiceOperationResult Install(ServiceInstallOptions options)
    {
        if (control is null)
            return new ServiceOperationResult(2, ["A Windows szolgáltatás csak Windowson telepíthető."]);
        var messages = new List<string>();
        // No account given: LocalSystem for a new service, the current account for an existing one (upgrade).
        var keepAccount = string.IsNullOrWhiteSpace(options.Account);
        var account = keepAccount ? "LocalSystem" : NormalizeAccount(options.Account!);
        var binPath = $"\"{options.ExePath}\"";
        var exists = control.Exists(ServiceName);

        if (exists)
        {
            List<string> config = ["config", ServiceName, "binPath=", binPath, "start=", "delayed-auto", "DisplayName=", DisplayName];
            if (!keepAccount)
            {
                config.AddRange(["obj=", account]);
                if (account != "LocalSystem")
                    config.AddRange(["password=", options.Password ?? ""]);
            }
            if (Sc(config, messages) is { } error)
                return error;
            messages.Add("A szolgáltatás beállításai frissítve.");
        }
        else
        {
            List<string> create = ["create", ServiceName, "binPath=", binPath, "start=", "delayed-auto", "DisplayName=", DisplayName, "obj=", account];
            if (account != "LocalSystem")
                create.AddRange(["password=", options.Password ?? ""]);
            if (Sc(create, messages) is { } error)
                return error;
            messages.Add($"A szolgáltatás létrehozva ({ServiceName}, fiók: {account}).");
        }

        if (Sc(["description", ServiceName, Description], messages) is { } descriptionError)
            return descriptionError;
        if (Sc(["failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"], messages) is { } failureError)
            return failureError;
        if (Sc(["failureflag", ServiceName, "1"], messages) is { } flagError)
            return flagError;
        messages.Add("Helyreállítás: hiba esetén újraindítás 1 perc múlva (az első három hibánál).");

        if (!keepAccount && account != "LocalSystem")
        {
            var grant = runner.Run("icacls.exe", [options.DataDir, "/grant", $"{account}:(OI)(CI)M", "/T", "/C", "/Q"]);
            if (grant.ExitCode != 0)
                return new ServiceOperationResult(1, [.. messages, $"A jogosultság beállítása nem sikerült ({options.DataDir}): {grant.Output}"]);
            messages.Add($"Módosítási jog megadva: {account} -> {options.DataDir}");
        }

        if (options.Start)
        {
            try
            {
                control.Start(ServiceName, StatusTimeout);
                messages.Add("A szolgáltatás fut.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
            {
                return new ServiceOperationResult(1, [.. messages, $"A szolgáltatás nem indult el: {ex.Message}. Részletek: logs mappa és Eseménynapló."]);
            }
        }
        return new ServiceOperationResult(0, messages);
    }

    public ServiceOperationResult Uninstall()
    {
        if (control is null)
            return new ServiceOperationResult(2, ["A Windows szolgáltatás csak Windowson kezelhető."]);
        if (!control.Exists(ServiceName))
            return new ServiceOperationResult(0, ["A szolgáltatás nincs telepítve."]);
        var messages = new List<string>();
        var stop = Stop();
        messages.AddRange(stop.Messages);
        if (Sc(["delete", ServiceName], messages) is { } error)
            return error;
        messages.Add("A szolgáltatás törölve.");
        return new ServiceOperationResult(0, messages);
    }

    public ServiceOperationResult Stop()
    {
        if (control is null || !control.Exists(ServiceName))
            return new ServiceOperationResult(0, ["A szolgáltatás nincs telepítve."]);
        try
        {
            control.Stop(ServiceName, StatusTimeout);
            return new ServiceOperationResult(0, ["A szolgáltatás leállítva."]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            return new ServiceOperationResult(1, [$"A szolgáltatás nem állt le: {ex.Message}"]);
        }
    }

    /// <summary>"user" means a local account (".\user"); "DOMAIN\user" and "user@domain" are kept.</summary>
    public static string NormalizeAccount(string account)
    {
        account = account.Trim();
        if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
            return "LocalSystem";
        return account.Contains('\\') || account.Contains('@') ? account : @".\" + account;
    }

    private ServiceOperationResult? Sc(IReadOnlyList<string> arguments, List<string> messages)
    {
        var result = runner.Run("sc.exe", arguments);
        if (result.ExitCode == 0)
            return null;
        var safe = string.Join(' ', arguments.Select((a, i) => i > 0 && arguments[i - 1] == "password=" ? "***" : a));
        return new ServiceOperationResult(1, [.. messages, $"sc.exe {safe} sikertelen ({result.ExitCode}): {result.Output}"]);
    }
}
