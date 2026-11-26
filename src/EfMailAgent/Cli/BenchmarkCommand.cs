using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using EfMailAgent.Admin;
using EfMailAgent.Hosting;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Processing;
using EfMailAgent.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace EfMailAgent.Cli;

/// <summary>benchmark: runs every sample email through a rule (like the Tesztkonzol, nothing is written) and reports timings.</summary>
internal static class BenchmarkCommand
{
    public static void Add(RootCommand root, Option<string?> dataDirOption, TextWriter output, TextWriter error)
    {
        var ruleId = new Argument<string>("szabaly") { Description = "A szabály azonosítója, pl. eloreendeles." };
        var dir = new Option<string?>("--dir") { Description = "A minta e-mailek mappája (alapból a samples\\emails).", HelpName = "mappa" };
        var repeat = new Option<int>("--repeat") { Description = "Hányszor fusson le minden levél (alapból 1).", DefaultValueFactory = _ => 1 };
        var command = new Command("benchmark", "Teljesítménymérés: a minta e-maileket lefuttatja a szabályon a beállított AI-val, és kiírja a levelenkénti és az átlagos időt. Semmit nem ír a sorba és a CSV-be.");
        command.Arguments.Add(ruleId);
        command.Options.Add(dir);
        command.Options.Add(repeat);
        command.SetAction(async (parse, cancellationToken) =>
        {
            using var context = AgentContextFactory.Create(parse.GetValue(dataDirOption), error, LogEventLevel.Warning);
            if (context is null)
                return ExitCodes.ConfigurationError;

            var services = new ServiceCollection()
                .AddSingleton(context.LoggerFactory)
                .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
                .AddEfMailAgentCore(context.Settings, startRuleWatcher: false);
            await using var provider = services.BuildServiceProvider();
            var set = provider.GetRequiredService<RuleRepository>().Current;
            var rule = set.FindById(parse.GetValue(ruleId)!);
            if (rule is null)
            {
                error.WriteLine($"Nincs ilyen érvényes szabály: '{parse.GetValue(ruleId)}'.");
                return ExitCodes.UsageError;
            }

            var samples = SampleEmails.Load(parse.GetValue(dir));
            if (samples.Count == 0)
            {
                error.WriteLine("Nem található minta e-mail (samples\\emails, vagy add meg: --dir).");
                return ExitCodes.UsageError;
            }

            var processor = provider.GetRequiredService<JobProcessor>();
            var llm = context.Settings.Settings.Llm;
            output.WriteLine($"Szabály: {rule.Id}, AI: {processor.Llm.Provider}{(llm.Model.Length > 0 ? " / " + (rule.Llm.Model ?? llm.Model) : "")}, levelek: {samples.Count}, ismétlés: {Math.Max(1, parse.GetValue(repeat))}");
            output.WriteLine();
            output.WriteLine($"{"FÁJL",-42} {"ÁLLAPOT",-14} {"SOR",4} {"IDŐ (mp)",9}");

            var durations = new List<double>();
            var failures = 0;
            for (var round = 0; round < Math.Max(1, parse.GetValue(repeat)); round++)
            {
                foreach (var sample in samples)
                {
                    var stopwatch = Stopwatch.StartNew();
                    string status;
                    int rows;
                    try
                    {
                        var result = await processor.ExtractAsync(rule, set.BaseSystemPrompt,
                            new PromptInput(sample.From, sample.Subject, sample.ReceivedAt, sample.Body, []), job: null, cancellationToken);
                        var final = result.Final;
                        rows = final.Rows.Count;
                        status = !final.IsValid ? JobStatus.Failed
                            : rows == 0 || final.Rows.Any(r => r.Status == RowStatus.NeedsReview) ? JobStatus.NeedsReview : JobStatus.Done;
                        if (result.RepairResponse is not null)
                            status += "*";
                    }
                    catch (LlmTransportException ex)
                    {
                        status = "hiba";
                        rows = 0;
                        failures++;
                        error.WriteLine($"{sample.File}: {ex.Message}");
                    }
                    var seconds = stopwatch.Elapsed.TotalSeconds;
                    durations.Add(seconds);
                    output.WriteLine(string.Create(CultureInfo.GetCultureInfo("hu-HU"), $"{Shorten(sample.File, 42),-42} {status,-14} {rows,4} {seconds,9:0.0}"));
                }
            }

            output.WriteLine();
            var hu = CultureInfo.GetCultureInfo("hu-HU");
            output.WriteLine(string.Format(hu, "Átlag: {0:0.0} mp/levél, leglassabb: {1:0.0} mp, összesen: {2:0.0} mp{3}  (* = javító hívás is kellett)",
                durations.Average(), durations.Max(), durations.Sum(), failures > 0 ? $", sikertelen hívás: {failures}" : ""));
            context.Log.Information("benchmark {RuleId}: átlag {Average:0.0} mp/levél, {Count} futás, {Provider} {Model}",
                rule.Id, durations.Average(), durations.Count, processor.Llm.Provider, rule.Llm.Model ?? llm.Model);
            return failures == 0 ? ExitCodes.Ok : ExitCodes.ProcessingFailed;
        });
        root.Subcommands.Add(command);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
