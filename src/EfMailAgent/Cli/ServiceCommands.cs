using System.CommandLine;
using System.Diagnostics;
using EfMailAgent.Configuration;
using EfMailAgent.Service;
using Serilog.Events;

namespace EfMailAgent.Cli;

/// <summary>configure, install-service, uninstall-service, stop-service, open-admin (used by the installer).</summary>
internal static class ServiceCommands
{
    public static void Add(RootCommand root, Option<string?> dataDirOption, TextWriter output, TextWriter error)
    {
        // configure
        var provider = new Option<string?>("--llm-provider") { Description = "ollama, openai_compatible vagy mock.", HelpName = "szolgáltató" };
        var baseUrl = new Option<string?>("--llm-base-url") { Description = "Az LLM szerver címe, pl. http://127.0.0.1:11434", HelpName = "url" };
        var model = new Option<string?>("--llm-model") { Description = "A modell neve, pl. qwen3.5:9b", HelpName = "modell" };
        var outputDir = new Option<string?>("--output") { Description = "A CSV kimeneti mappa.", HelpName = "mappa" };
        var configure = new Command("configure", "Létrehozza az adatmappát (első futáskor tokennel és mintaszabályokkal), és beállítja a megadott értékeket. A meglévő beállításokat nem írja felül.");
        configure.Options.Add(provider);
        configure.Options.Add(baseUrl);
        configure.Options.Add(model);
        configure.Options.Add(outputDir);
        configure.SetAction(parse =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;
            var (p, u, m, o) = (parse.GetValue(provider), parse.GetValue(baseUrl), parse.GetValue(model), parse.GetValue(outputDir));
            if (p is not null || u is not null || m is not null || o is not null)
            {
                var errors = SettingsEditor.Update(context.Settings.SettingsFile, root =>
                {
                    var llm = SettingsEditor.Section(root, "Llm");
                    if (p is not null)
                        llm["Provider"] = p.Trim();
                    if (u is not null)
                        llm["BaseUrl"] = u.Trim();
                    if (m is not null)
                        llm["Model"] = m.Trim();
                    if (o is not null)
                        SettingsEditor.Section(root, "Paths")["Output"] = o.Trim();
                });
                if (errors.Count > 0)
                {
                    error.WriteLine("A beállítások nem menthetők:");
                    foreach (var e in errors)
                        error.WriteLine($"  - {e}");
                    return ExitCodes.ConfigurationError;
                }
                if (o is not null)
                    Directory.CreateDirectory(Path.IsPathRooted(o) ? o : Path.Combine(context.Settings.DataDirectory.Path, o));
            }
            output.WriteLine($"Adatmappa: {context.Settings.DataDirectory.Path}");
            output.WriteLine($"Beállítások: {context.Settings.SettingsFile}");
            return ExitCodes.Ok;
        });
        root.Subcommands.Add(configure);

        // install-service
        var account = new Option<string?>("--account") { Description = "Szolgáltatásfiók (alapból LocalSystem), pl. TARTOMANY\\felhasznalo.", HelpName = "fiók" };
        var password = new Option<string?>("--password") { Description = "A szolgáltatásfiók jelszava.", HelpName = "jelszó" };
        var noStart = new Option<bool>("--no-start") { Description = "Ne indítsa el a szolgáltatást." };
        var install = new Command("install-service", "Telepíti (vagy frissíti) az EfMailAgent Windows szolgáltatást: automatikus (késleltetett) indítás, újraindítás hiba esetén.");
        install.Options.Add(account);
        install.Options.Add(password);
        install.Options.Add(noStart);
        install.SetAction(parse =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EfMailAgent.exe");
            var result = ServiceManager.CreateDefault().Install(new ServiceInstallOptions(exe, context.Settings.DataDirectory.Path,
                parse.GetValue(account), parse.GetValue(password), !parse.GetValue(noStart)));
            return Report(context, "install-service", result, output, error);
        });
        root.Subcommands.Add(install);

        var uninstall = new Command("uninstall-service", "Leállítja és törli az EfMailAgent Windows szolgáltatást (az adatok megmaradnak).");
        uninstall.SetAction(parse => Report(null, "uninstall-service", ServiceManager.CreateDefault().Uninstall(), output, error));
        root.Subcommands.Add(uninstall);

        var stop = new Command("stop-service", "Leállítja az EfMailAgent Windows szolgáltatást (a futó feladat visszakerül a sorba).");
        stop.SetAction(parse => Report(null, "stop-service", ServiceManager.CreateDefault().Stop(), output, error));
        root.Subcommands.Add(stop);

        // open-admin
        var openAdmin = new Command("open-admin", "Megnyitja a kezelőfelületet a böngészőben, bejelentkezve.");
        var pageOption = new Option<string?>("--page") { Description = "A megnyitandó oldal: attekintes, szabalyok, tesztkonzol, beallitasok, naplo." };
        openAdmin.Options.Add(pageOption);
        openAdmin.SetAction(async (parse, cancellationToken) =>
        {
            // Read-only (also run by the installer as the normal user): nothing is created or written.
            var settings = AdminLauncher.LoadReadOnly(parse.GetValue(dataDirOption));
            if (settings is null)
            {
                error.WriteLine("Nincsenek beállítások (appsettings.json). Indítsd el egyszer az EfMailAgent.exe-t, vagy telepítsd a telepítővel.");
                return ExitCodes.ConfigurationError;
            }
            if (!await AdminLauncher.IsRunningAsync(settings, cancellationToken))
                error.WriteLine("Figyelem: az EfMailAgent most nem válaszol. Indítsd el (EfMailAgent.exe vagy a szolgáltatás), különben a böngésző nem tudja megnyitni a felületet.");
            // Logged in through a one-time link: the UI opens without asking for the token.
            output.WriteLine(AdminLauncher.PlainUrl(settings));
            var failure = AdminLauncher.TryOpen(AdminLauncher.LoginUrl(settings, parse.GetValue(pageOption)));
            if (failure is not null)
                error.WriteLine($"A böngésző nem nyitható meg: {failure}");
            return ExitCodes.Ok;
        });
        root.Subcommands.Add(openAdmin);
    }

    private static int Report(AgentContext? context, string command, ServiceOperationResult result, TextWriter output, TextWriter error)
    {
        foreach (var message in result.Messages)
            (result.Success ? output : error).WriteLine(message);
        context?.Log.Information("{Command}: {Messages}", command, string.Join(" | ", result.Messages));
        return result.ExitCode;
    }
}
