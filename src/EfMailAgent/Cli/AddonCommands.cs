using System.CommandLine;
using EfMailAgent.Addon;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace EfMailAgent.Cli;

/// <summary>install-addon, uninstall-addon, addon-status.</summary>
internal static class AddonCommands
{
    public static void Add(RootCommand root, Option<string?> dataDirOption, TextWriter output, TextWriter error)
    {
        var thunderbirdDir = new Option<string?>("--thunderbird-dir")
        {
            Description = "A Thunderbird telepítési mappája (alapból a registryből és a Program Files mappákból keresi).",
            HelpName = "mappa",
        };
        var xpi = new Option<string?>("--xpi") { Description = "Az add-on XPI (alapból az exe melletti efmailagent.xpi).", HelpName = "fájl" };
        var profile = new Option<string?>("--profile") { Description = "Profil mód: a Thunderbird profil mappája.", HelpName = "mappa" };
        var profileRoot = new Option<string?>("--user-profile-root")
        {
            Description = "A profiles.ini helye (alapból %APPDATA%\\Thunderbird; szolgáltatásként a felhasználóét kell megadni).",
            HelpName = "mappa",
        };
        var mode = new Option<string>("--mode") { Description = "policy (alapértelmezés, rendszergazdai jog kell) vagy profile.", DefaultValueFactory = _ => "policy" };
        mode.AcceptOnlyFromAmong("policy", "profile");

        var install = new Command("install-addon", "Telepíti a Thunderbird add-ont (policies.json vagy profil), és beállítja a szerver címét és a tokent.");
        install.Options.Add(mode);
        install.Options.Add(profile);
        install.Options.Add(xpi);
        install.Options.Add(thunderbirdDir);
        install.Options.Add(profileRoot);
        install.SetAction(parse =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;
            var installer = CreateInstaller(context, parse.GetValue(thunderbirdDir));
            var result = installer.Install(new InstallOptions(parse.GetValue(mode) == "profile" ? InstallMode.Profile : InstallMode.Policy,
                parse.GetValue(xpi), parse.GetValue(profile), parse.GetValue(profileRoot)));
            return Report(context, "install-addon", result, output, error);
        });
        root.Subcommands.Add(install);

        var uninstall = new Command("uninstall-addon", "Eltávolítja az EfMailAgent bejegyzéseit a policies.json-ból (a többi beállítás megmarad) és a profilból.");
        uninstall.Options.Add(thunderbirdDir);
        uninstall.Options.Add(profile);
        uninstall.Options.Add(profileRoot);
        uninstall.SetAction(parse =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;
            var result = CreateInstaller(context, parse.GetValue(thunderbirdDir))
                .Uninstall(new UninstallOptions(parse.GetValue(profile), parse.GetValue(profileRoot)));
            return Report(context, "uninstall-addon", result, output, error);
        });
        root.Subcommands.Add(uninstall);

        var status = new Command("addon-status", "Megmutatja a Thunderbird és az add-on telepítésének állapotát.");
        status.Options.Add(thunderbirdDir);
        status.Options.Add(xpi);
        status.SetAction(parse =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;
            var addonStatus = CreateInstaller(context, parse.GetValue(thunderbirdDir)).GetStatus(parse.GetValue(xpi));
            PrintStatus(addonStatus, output);
            return addonStatus.Ok ? ExitCodes.Ok : AddonExitCodes.NotOk;
        });
        root.Subcommands.Add(status);
    }

    private static AddonInstaller CreateInstaller(AgentContext context, string? thunderbirdDir)
    {
        IThunderbirdLocator locator = thunderbirdDir is not null ? new FixedThunderbirdLocator(thunderbirdDir) : ThunderbirdLocator.CreateDefault();
        return new AddonInstaller(context.Settings, locator, logger: context.LoggerFactory.CreateLogger<AddonInstaller>());
    }

    private static int Report(AgentContext context, string command, AddonOperationResult result, TextWriter output, TextWriter error)
    {
        var writer = result.Success ? output : error;
        foreach (var message in result.Messages)
            writer.WriteLine(message);
        if (result.Success)
            context.Log.Information("{Command} sikeres: {Messages}", command, string.Join(" | ", result.Messages));
        else
            context.Log.Warning("{Command} sikertelen ({ExitCode}): {Messages}", command, result.ExitCode, string.Join(" | ", result.Messages));
        return result.ExitCode;
    }

    internal static void PrintStatus(AddonStatus status, TextWriter output)
    {
        string Mark(bool ok) => ok ? "rendben" : "HIBA";

        output.WriteLine($"Beépített add-on (XPI):  {(status.Bundled is null ? status.BundledError : $"{status.Bundled.Version} ({status.Bundled.Path})")}");
        output.WriteLine($"Telepített XPI:          {(status.InstalledXpi is null ? "nincs" : $"{status.InstalledXpi.Version} ({status.InstalledXpi.Path})")}");
        output.WriteLine($"Telepítés módja:         {status.State?.Mode ?? "még nem futott install-addon"}");
        output.WriteLine($"Thunderbird fut:         {(status.ThunderbirdRunning ? "igen" : "nem")}");
        if (status.Installations.Count == 0)
            output.WriteLine("Thunderbird:             nem található");

        foreach (var i in status.Installations)
        {
            output.WriteLine();
            output.WriteLine($"Thunderbird:             {i.Installation.InstallDir} ({i.Installation.Version ?? "ismeretlen verzió"}, forrás: {i.Installation.Source})");
            output.WriteLine($"  policies.json:         {i.PolicyPath} ({i.FileState switch
            {
                PolicyFileState.Missing => "nincs",
                PolicyFileState.Empty => "üres",
                PolicyFileState.Malformed => "HIBÁS JSON: " + i.FileError,
                _ => "megvan",
            }})");
            output.WriteLine($"  EfMailAgent bejegyzés: {(i.Entry is null ? "HIÁNYZIK" : $"{i.InstallationMode}, {i.Entry.InstallUrl}")}");
            if (i.Entry is not null)
            {
                output.WriteLine($"  XPI útvonal:           {Mark(i.InstallUrlOk)}");
                output.WriteLine($"  Szerver cím:           {Mark(i.ServerUrlOk)} ({i.Entry.ServerUrl})");
                output.WriteLine($"  Token:                 {Mark(i.TokenOk)}");
            }
        }

        output.WriteLine();
        output.WriteLine(status.Ok ? "Állapot: RENDBEN" : "Állapot: JAVÍTANDÓ (EfMailAgent install-addon, rendszergazdaként)");
    }
}
