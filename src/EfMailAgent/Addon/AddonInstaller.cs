using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfMailAgent.Addon;

public enum InstallMode
{
    Policy,
    Profile,
}

public static class AddonExitCodes
{
    public const int Ok = 0;
    public const int NotOk = 1;
    public const int AddonError = 6;
    public const int NotElevated = 7;
    public const int PolicyFileMalformed = 8;
}

public sealed record InstallOptions(InstallMode Mode = InstallMode.Policy, string? XpiPath = null, string? ProfilePath = null, string? UserProfileRoot = null);

public sealed record UninstallOptions(string? ProfilePath = null, string? UserProfileRoot = null);

public sealed record AddonOperationResult(int ExitCode, IReadOnlyList<string> Messages)
{
    public bool Success => ExitCode == AddonExitCodes.Ok;
}

public sealed record InstallationStatus(
    ThunderbirdInstallation Installation,
    string PolicyPath,
    PolicyFileState FileState,
    string? FileError,
    AddonPolicy? Entry,
    string? InstallationMode,
    bool InstallUrlOk,
    bool ServerUrlOk,
    bool TokenOk)
{
    public bool Ok => Entry is not null && InstallationMode == "force_installed" && InstallUrlOk && ServerUrlOk && TokenOk;
}

public sealed record AddonStatus(
    IReadOnlyList<InstallationStatus> Installations,
    AddonPackage? Bundled,
    string? BundledError,
    AddonPackage? InstalledXpi,
    InstallState? State,
    bool ThunderbirdRunning)
{
    public bool Ok => Bundled is not null && Installations.Count > 0 && Installations.All(i => i.Ok)
                      && InstalledXpi is not null && InstalledXpi.Version == Bundled.Version;
}

/// <summary>Written by install-addon, removed by uninstall-addon; self-heal only acts while it says "policy".</summary>
public sealed record InstallState(string Mode, string Version, IReadOnlyList<string> ThunderbirdDirs, DateTimeOffset InstalledAt);

public interface IElevationCheck
{
    bool IsElevated();
}

public sealed class SystemElevationCheck : IElevationCheck
{
    public bool IsElevated() => !OperatingSystem.IsWindows() || IsWindowsAdmin();

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Installs, repairs and removes the Thunderbird add-on (CLAUDE.md section 10). Policy mode merges our entries into
/// &lt;Thunderbird&gt;\distribution\policies.json (backed up first, never overwritten blindly); profile mode copies the
/// XPI into a profile's extensions folder.
/// </summary>
public sealed class AddonInstaller(
    EffectiveSettings settings,
    IThunderbirdLocator locator,
    IElevationCheck? elevation = null,
    TimeProvider? timeProvider = null,
    ILogger<AddonInstaller>? logger = null,
    string? bundledXpiPath = null)
{
    public const string StateFileName = "install-state.json";

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IElevationCheck _elevation = elevation ?? new SystemElevationCheck();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public string ServerUrl => $"http://127.0.0.1:{settings.Settings.Server.Port}";

    public string StatePath => Path.Combine(settings.AddonDir, StateFileName);

    public AddonOperationResult Install(InstallOptions options)
    {
        var messages = new List<string>();
        AddonPackage package;
        try
        {
            package = LoadPackage(options.XpiPath);
        }
        catch (AddonException ex)
        {
            return new AddonOperationResult(AddonExitCodes.AddonError, [ex.Message]);
        }

        return options.Mode == InstallMode.Profile
            ? InstallToProfile(package, options, messages)
            : InstallPolicy(package, messages, requireElevation: true);
    }

    public AddonOperationResult Uninstall(UninstallOptions options)
    {
        var messages = new List<string>();
        var exitCode = AddonExitCodes.Ok;

        foreach (var installation in locator.FindInstallations())
        {
            var path = PolicyFile.PolicyPath(installation.InstallDir);
            var content = PolicyFile.Load(path);
            switch (content.State)
            {
                case PolicyFileState.Malformed:
                    messages.Add($"A policies.json hibás, nem módosítottam: {path} ({content.Error})");
                    exitCode = AddonExitCodes.PolicyFileMalformed;
                    continue;
                case PolicyFileState.Missing or PolicyFileState.Empty:
                    messages.Add($"Nincs EfMailAgent bejegyzés: {path}");
                    continue;
            }

            if (PolicyFile.Read(content.Root, AddonPackage.ExpectedId) is null)
            {
                messages.Add($"Nincs EfMailAgent bejegyzés: {path}");
                continue;
            }

            try
            {
                var backup = PolicyFile.Backup(path, _time.GetLocalNow());
                PolicyFile.Write(path, PolicyFile.Remove(content.Root!, AddonPackage.ExpectedId));
                messages.Add($"EfMailAgent bejegyzések eltávolítva: {path} (mentés: {backup})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"A policies.json nem írható ({path}): {ex.Message}. Rendszergazdaként futtasd.");
                exitCode = AddonExitCodes.NotElevated;
            }
        }

        foreach (var profile in TargetProfiles(options.ProfilePath, options.UserProfileRoot, allProfiles: true))
        {
            var xpi = ThunderbirdProfiles.ExtensionPath(profile.Path);
            if (!File.Exists(xpi))
                continue;
            File.Delete(xpi);
            messages.Add($"Add-on eltávolítva a profilból: {xpi}");
        }

        if (File.Exists(StatePath))
            File.Delete(StatePath);
        messages.Add("Az add-on a Thunderbird profilban a következő indítás után is megmaradhat, de már nem kötelező; " +
                     "a Kiegészítők kezelőjében eltávolítható. A változás a Thunderbird újraindítása után érvényes.");
        return new AddonOperationResult(exitCode, messages);
    }

    public AddonStatus GetStatus(string? xpiPath = null)
    {
        AddonPackage? bundled = null;
        string? bundledError = null;
        try
        {
            bundled = LoadPackage(xpiPath);
        }
        catch (AddonException ex)
        {
            bundledError = ex.Message;
        }

        AddonPackage? installed = null;
        if (bundled is not null && File.Exists(AddonPackage.InstalledPath(settings, bundled)))
            installed = TryRead(AddonPackage.InstalledPath(settings, bundled));
        installed ??= Directory.Exists(settings.AddonDir)
            ? Directory.GetFiles(settings.AddonDir, "efmailagent-*.xpi").Select(TryRead).OfType<AddonPackage>().FirstOrDefault()
            : null;

        var expectedUrl = bundled is null ? null : FileUrl(AddonPackage.InstalledPath(settings, bundled));
        var installations = locator.FindInstallations().Select(installation =>
        {
            var path = PolicyFile.PolicyPath(installation.InstallDir);
            var content = PolicyFile.Load(path);
            var entry = PolicyFile.Read(content.Root, AddonPackage.ExpectedId);
            return new InstallationStatus(installation, path, content.State, content.Error, entry,
                PolicyFile.InstallationMode(content.Root, AddonPackage.ExpectedId),
                entry is not null && expectedUrl is not null && string.Equals(entry.InstallUrl, expectedUrl, StringComparison.OrdinalIgnoreCase),
                entry?.ServerUrl == ServerUrl,
                entry is not null && entry.Token == settings.Settings.Server.Token);
        }).ToArray();

        return new AddonStatus(installations, bundled, bundledError, installed, ReadState(), locator.IsRunning());
    }

    /// <summary>
    /// Service start: if the add-on was installed in policy mode and the entry is missing or outdated (version, token,
    /// port), repair it. Never throws; without Thunderbird or without an install-addon run it does nothing.
    /// </summary>
    public AddonOperationResult SelfHeal()
    {
        try
        {
            var state = ReadState();
            if (state is null || state.Mode != "policy")
            {
                _logger.LogDebug("Add-on önjavítás kihagyva: az install-addon (policy mód) még nem futott.");
                return new AddonOperationResult(AddonExitCodes.Ok, ["Önjavítás kihagyva (nincs policy módú telepítés)."]);
            }

            var status = GetStatus();
            if (status.Bundled is null)
            {
                _logger.LogWarning("Add-on önjavítás kihagyva: {Error}", status.BundledError);
                return new AddonOperationResult(AddonExitCodes.AddonError, [status.BundledError ?? ""]);
            }
            if (status.Installations.Count == 0)
            {
                _logger.LogInformation("Add-on önjavítás kihagyva: a Thunderbird nem található ezen a gépen.");
                return new AddonOperationResult(AddonExitCodes.Ok, ["A Thunderbird nem található."]);
            }
            if (status.Ok)
                return new AddonOperationResult(AddonExitCodes.Ok, ["Az add-on telepítése rendben van."]);

            var reasons = string.Join(", ", status.Installations.Where(i => !i.Ok).Select(Describe)
                .Append(status.InstalledXpi?.Version != status.Bundled.Version ? "az XPI verziója eltér" : null).OfType<string>());
            _logger.LogWarning("Az add-on policy bejegyzése hiányzik vagy elavult ({Reasons}), javítás következik.", reasons);
            var result = InstallPolicy(status.Bundled, [], requireElevation: false);
            foreach (var message in result.Messages)
                _logger.Log(result.Success ? LogLevel.Information : LogLevel.Warning, "Add-on önjavítás: {Message}", message);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Az add-on önjavítása nem sikerült.");
            return new AddonOperationResult(AddonExitCodes.AddonError, [ex.Message]);
        }
    }

    public static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private AddonOperationResult InstallPolicy(AddonPackage package, List<string> messages, bool requireElevation)
    {
        var installations = locator.FindInstallations();
        if (installations.Count == 0)
            return new AddonOperationResult(AddonExitCodes.AddonError,
                ["A Thunderbird nem található (registry, Program Files). Add meg a mappáját: --thunderbird-dir \"C:\\Program Files\\Mozilla Thunderbird\""]);

        if (requireElevation && !_elevation.IsElevated())
            return new AddonOperationResult(AddonExitCodes.NotElevated,
                ["Az install-addon rendszergazdai jogot igényel (a Thunderbird program mappájába ír). Indítsd a parancssort rendszergazdaként, vagy használd a --mode profile kapcsolót."]);

        // Validate every policies.json before touching anything.
        var contents = installations.Select(i => (Installation: i, Path: PolicyFile.PolicyPath(i.InstallDir))).Select(x => (x.Installation, x.Path, Content: PolicyFile.Load(x.Path))).ToArray();
        var malformed = contents.Where(c => c.Content.State == PolicyFileState.Malformed).ToArray();
        if (malformed.Length > 0)
            return new AddonOperationResult(AddonExitCodes.PolicyFileMalformed, malformed
                .Select(c => $"A policies.json hibás JSON, ezért nem módosítottam: {c.Path} ({c.Content.Error}). Javítsd vagy nevezd át, majd futtasd újra.")
                .ToArray());

        try
        {
            Directory.CreateDirectory(settings.AddonDir);
            var target = AddonPackage.InstalledPath(settings, package);
            if (!string.Equals(Path.GetFullPath(package.Path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(package.Path, target, overwrite: true);
            foreach (var old in Directory.GetFiles(settings.AddonDir, "efmailagent-*.xpi").Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
                File.Delete(old);
            messages.Add($"Add-on csomag ({package.Id} {package.Version}): {target}");

            var policy = new AddonPolicy(package.Id, FileUrl(target), ServerUrl, settings.Settings.Server.Token);
            foreach (var (installation, path, content) in contents)
            {
                var merged = PolicyFile.Merge(content.Root, policy);
                if (content.Root is not null && JsonNode.DeepEquals(content.Root, merged))
                {
                    messages.Add($"A policies.json már naprakész: {path}");
                    continue;
                }
                var backup = PolicyFile.Backup(path, _time.GetLocalNow());
                PolicyFile.Write(path, merged);
                messages.Add(backup is null ? $"policies.json létrehozva: {path}" : $"policies.json frissítve: {path} (mentés: {backup})");
                messages.Add($"Thunderbird: {installation.InstallDir} ({installation.Version ?? "ismeretlen verzió"})");
            }

            WriteState(new InstallState("policy", package.Version, installations.Select(i => i.InstallDir).ToArray(), _time.GetUtcNow()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AddonOperationResult(AddonExitCodes.NotElevated,
                [.. messages, $"Írási hiba: {ex.Message}. Rendszergazdaként futtasd az install-addon parancsot."]);
        }

        messages.Add(locator.IsRunning()
            ? "A Thunderbird most fut: indítsd újra, hogy az add-on települjön és megkapja a beállításokat."
            : "A Thunderbird következő indításakor az add-on automatikusan települ és beállítódik.");
        return new AddonOperationResult(AddonExitCodes.Ok, messages);
    }

    private AddonOperationResult InstallToProfile(AddonPackage package, InstallOptions options, List<string> messages)
    {
        var profiles = TargetProfiles(options.ProfilePath, options.UserProfileRoot, allProfiles: false);
        if (profiles.Count == 0)
            return new AddonOperationResult(AddonExitCodes.AddonError,
                [$"Nem található Thunderbird profil ({options.UserProfileRoot ?? ThunderbirdProfiles.DefaultRoot}\\profiles.ini). Add meg: --profile <profilmappa>"]);

        try
        {
            foreach (var profile in profiles)
            {
                var target = ThunderbirdProfiles.ExtensionPath(profile.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(package.Path, target, overwrite: true);
                messages.Add($"Add-on bemásolva a(z) '{profile.Name}' profilba: {target}");
            }
            WriteState(new InstallState("profile", package.Version, [], _time.GetUtcNow()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AddonOperationResult(AddonExitCodes.AddonError, [.. messages, $"Írási hiba: {ex.Message}"]);
        }

        messages.Add("Profil módban a Thunderbird indításkor megkérdezi, engedélyezed-e az add-ont. A szerver címét és a tokent az add-on beállításainál kell megadni:");
        messages.Add($"  Szerver: {ServerUrl}   Token: EfMailAgent print-token");
        messages.Add(locator.IsRunning() ? "A Thunderbird most fut: indítsd újra." : "A Thunderbird következő indításakor jelenik meg.");
        return new AddonOperationResult(AddonExitCodes.Ok, messages);
    }

    private static IReadOnlyList<ThunderbirdProfile> TargetProfiles(string? profilePath, string? userProfileRoot, bool allProfiles)
    {
        if (!string.IsNullOrWhiteSpace(profilePath))
            return [new ThunderbirdProfile(Path.GetFileName(Path.GetFullPath(profilePath).TrimEnd(Path.DirectorySeparatorChar)), Path.GetFullPath(profilePath), true)];
        var profiles = ThunderbirdProfiles.Read(userProfileRoot ?? ThunderbirdProfiles.DefaultRoot);
        if (allProfiles)
            return profiles;
        var defaults = profiles.Where(p => p.IsDefault).ToArray();
        return defaults.Length > 0 ? defaults : profiles.Take(1).ToArray();
    }

    private AddonPackage LoadPackage(string? xpiPath)
    {
        var path = xpiPath ?? bundledXpiPath ?? AddonPackage.FindBundled()
            ?? throw new AddonException("Nem található az add-on csomag (efmailagent.xpi az exe mellett). Add meg: --xpi <útvonal>");
        if (!File.Exists(path))
            throw new AddonException($"Az XPI nem található: {path}");
        var package = AddonPackage.Read(path);
        if (package.Id != AddonPackage.ExpectedId)
            throw new AddonException($"Az XPI azonosítója '{package.Id}', de '{AddonPackage.ExpectedId}' kell.");
        return package;
    }

    private static AddonPackage? TryRead(string path)
    {
        try
        {
            return AddonPackage.Read(path);
        }
        catch (AddonException)
        {
            return null;
        }
    }

    private InstallState? ReadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(StatePath), StateJson) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteState(InstallState state)
    {
        Directory.CreateDirectory(settings.AddonDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, StateJson));
    }

    private static string Describe(InstallationStatus status) =>
        status.Entry is null ? $"nincs bejegyzés ({status.PolicyPath})"
        : !status.InstallUrlOk ? "más XPI-re mutat"
        : !status.TokenOk ? "a token eltér"
        : !status.ServerUrlOk ? "a szerver címe eltér"
        : status.InstallationMode != "force_installed" ? "nem kötelező telepítés"
        : "ismeretlen eltérés";
}
