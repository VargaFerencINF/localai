using System.CommandLine;
using EfMailAgent.Rules;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace EfMailAgent.Cli;

public static class CliApp
{
    public static RootCommand BuildRootCommand(TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        var dataDirOption = new Option<string?>("--data-dir")
        {
            Description = "Adatmappa (appsettings.json, rules, output, data, logs). Alapértelmezés: C:\\ProgramData\\EfMailAgent, fejlesztéskor ./.devdata",
            Recursive = true,
            HelpName = "mappa",
        };

        var root = new RootCommand("EfMailAgent: továbbított e-mailek feldolgozása helyi AI-val, strukturált CSV kimenettel.");
        root.Options.Add(dataDirOption);

        // validate-rules
        var watchOption = new Option<bool>("--watch")
        {
            Description = "Figyeli a szabálymappát, és minden változás után újra kiírja az eredményt (kilépés: Ctrl+C).",
        };
        var validateRules = new Command("validate-rules", "Betölti és ellenőrzi a szabályokat, majd táblázatban kiírja az eredményt.");
        validateRules.Options.Add(watchOption);
        validateRules.SetAction(async (parse, cancellationToken) =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Information);
            if (context is null)
                return ExitCodes.ConfigurationError;
            return await ValidateRulesCommand.RunAsync(context, parse.GetValue(watchOption), output, cancellationToken);
        });
        root.Subcommands.Add(validateRules);

        // print-token
        var printToken = new Command("print-token", "Kiírja az API tokent (X-EfMailAgent-Token), amelyet az add-on használ.");
        printToken.SetAction(parse =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;
            output.WriteLine(context.Settings.Settings.Server.Token);
            return ExitCodes.Ok;
        });
        root.Subcommands.Add(printToken);

        // test-rule
        var ruleIdArgument = new Argument<string>("szabaly") { Description = "A szabály azonosítója (pl. eloreendeles)." };
        var fileOption = new Option<string>("--file") { Description = "E-mail fájl: .eml (fejlécekkel) vagy .txt (csak a törzs).", Required = true, HelpName = "fájl" };
        var subjectOption = new Option<string?>("--subject") { Description = "Tárgy (felülírja az .eml tárgyát).", HelpName = "tárgy" };
        var fromOption = new Option<string?>("--from") { Description = "Feladó (felülírja az .eml feladóját).", HelpName = "feladó" };
        var writeOption = new Option<bool>("--write") { Description = "A teljes folyamat a soron keresztül fut, és CSV-t ír." };
        var newIdOption = new Option<bool>("--new-message-id") { Description = "--write mellett új Message-ID-t használ (ismételt teszteléshez)." };
        var testRule = new Command("test-rule", "Egy e-mailt lefuttat egy szabályon: kiírja a promptot, a nyers választ és a sorokat. --write nélkül semmit nem ír.");
        testRule.Arguments.Add(ruleIdArgument);
        testRule.Options.Add(fileOption);
        testRule.Options.Add(subjectOption);
        testRule.Options.Add(fromOption);
        testRule.Options.Add(writeOption);
        testRule.Options.Add(newIdOption);
        testRule.SetAction(async (parse, cancellationToken) =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;
            var options = new TestRuleOptions(parse.GetValue(ruleIdArgument)!, parse.GetValue(fileOption)!, parse.GetValue(subjectOption),
                parse.GetValue(fromOption), parse.GetValue(writeOption), parse.GetValue(newIdOption));
            return await TestRuleCommand.RunAsync(context, options, output, error, cancellationToken);
        });
        root.Subcommands.Add(testRule);

        AddonCommands.Add(root, dataDirOption, output, error);
        ServiceCommands.Add(root, dataDirOption, output, error);
        BenchmarkCommand.Add(root, dataDirOption, output, error);

        // No subcommand: run the worker and the HTTP API (console window or Windows service). Started by a person, it also
        // opens the admin UI in the browser; if it already runs, it only opens the UI.
        var noBrowserOption = new Option<bool>("--no-browser") { Description = "Ne nyissa meg a kezelőfelületet a böngészőben (szkriptekhez)." };
        root.Options.Add(noBrowserOption);
        root.SetAction(async (parse, cancellationToken) =>
        {
            var openBrowser = AdminLauncher.ShouldOpenBrowser(parse.GetValue(noBrowserOption));
            // Double click on Windows: behave like a desktop app (no console window, errors in a message box).
            var desktop = openBrowser && OperatingSystem.IsWindows() && WindowsConsole.DetachIfOwnConsole();
            var errorWriter = desktop ? new StringWriter() : error;
            var outputWriter = desktop ? TextWriter.Null : output;

            if (openBrowser && await AdminLauncher.OpenRunningInstanceAsync(parse.GetValue(dataDirOption), outputWriter, cancellationToken))
                return ExitCodes.Ok;
            int code;
            using (var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), errorWriter, LogEventLevel.Information))
                code = context is null ? ExitCodes.ConfigurationError : await RunCommand.RunAsync(context, openBrowser, cancellationToken);
            if (code != ExitCodes.Ok && openBrowser)
            {
                if (desktop && OperatingSystem.IsWindows())
                {
                    WindowsConsole.ShowError(StartFailureText(code, errorWriter.ToString() ?? ""));
                }
                else
                {
                    // Started from a console: keep it open so the error can be read.
                    error.WriteLine();
                    error.WriteLine("Az EfMailAgent nem tudott elindulni (a hiba oka fent olvasható). Nyomj Entert a bezáráshoz.");
                    Console.ReadLine();
                }
            }
            return code;
        });

        return root;
    }

    internal static string StartFailureText(int code, string details) =>
        "Az EfMailAgent nem tudott elindulni.\n\n" + (code switch
        {
            ExitCodes.PortInUse => "A 127.0.0.1-en beállított portot (alapból 47811) egy másik program használja.",
            ExitCodes.ConfigurationError => details.Trim().Length > 0 ? details.Trim() : "A beállítások hibásak vagy az adatmappa nem írható.",
            _ => $"Hibakód: {code}.",
        }) + "\n\nRészletek a naplóban: C:\\ProgramData\\EfMailAgent\\logs";

    public static Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        var root = BuildRootCommand(output, error);
        var config = new InvocationConfiguration();
        if (output is not null)
            config.Output = output;
        if (error is not null)
            config.Error = error;
        return root.Parse(args).InvokeAsync(config);
    }
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int InvalidRules = 1;
    public const int ConfigurationError = 2;
    public const int UsageError = 3;
    public const int ProcessingFailed = 4;
    public const int PortInUse = 5;
}

internal static class ValidateRulesCommand
{
    public static async Task<int> RunAsync(AgentContext context, bool watch, TextWriter output, CancellationToken cancellationToken)
    {
        var rulesDir = context.Settings.RulesDir;
        if (!watch)
        {
            var set = RuleLoader.LoadAll(rulesDir);
            RuleTablePrinter.Print(set, context.Settings, output);
            context.Log.Information("validate-rules: {Valid} érvényes, {Invalid} hibás szabály, verzió {Version}",
                set.Rules.Count, set.InvalidFolders.Count, set.Version);
            return set.InvalidFolders.Count == 0 ? ExitCodes.Ok : ExitCodes.InvalidRules;
        }

        using var repository = new RuleRepository(rulesDir, context.LoggerFactory.CreateLogger<RuleRepository>());
        repository.Reloaded += (_, set) =>
        {
            lock (output)
            {
                output.WriteLine();
                output.WriteLine($"--- {DateTime.Now:HH:mm:ss} ---");
                RuleTablePrinter.Print(set, context.Settings, output);
            }
        };
        repository.Start();
        output.WriteLine("Figyelés bekapcsolva, kilépés: Ctrl+C.");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        return repository.Current.InvalidFolders.Count == 0 ? ExitCodes.Ok : ExitCodes.InvalidRules;
    }
}
