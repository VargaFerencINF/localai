using EfMailAgent.Configuration;
using EfMailAgent.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace EfMailAgent.Cli;

/// <summary>Everything a command needs after first-run initialization.</summary>
public sealed class AgentContext(EffectiveSettings settings, Serilog.ILogger serilog, ILoggerFactory loggerFactory, BootstrapReport? report = null) : IDisposable
{
    public EffectiveSettings Settings { get; } = settings;

    /// <summary>What the first-run initialization did (settings created, samples copied).</summary>
    public BootstrapReport Report { get; } = report ?? new BootstrapReport(false, false, null);
    public Serilog.ILogger Serilog { get; } = serilog;

    /// <summary>Logger for messages of the application itself (not of a specific class).</summary>
    public Serilog.ILogger Log { get; } = serilog.ForContext("SourceContext", "EfMailAgent");
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    public void Dispose()
    {
        LoggerFactory.Dispose();
        (Serilog as IDisposable)?.Dispose();
    }
}

public static class AgentContextFactory
{
    /// <summary>
    /// Resolves the data folder, creates it on first run, loads appsettings.json and sets up Serilog.
    /// Returns null (after printing Hungarian errors to <paramref name="error"/>) if the settings are invalid.
    /// </summary>
    public static AgentContext? Create(string? dataDirArgument, TextWriter error, LogEventLevel consoleLevel)
    {
        var dataDirectory = DataDirectory.Resolve(dataDirArgument);

        EffectiveSettings settings;
        BootstrapReport report;
        try
        {
            (settings, report) = SettingsBootstrapper.Initialize(dataDirectory);
        }
        catch (SettingsLoadException ex)
        {
            error.WriteLine($"Hibás beállítások ({Path.Combine(dataDirectory.Path, SettingsBootstrapper.SettingsFileName)}):");
            foreach (var message in ex.Errors)
                error.WriteLine($"  - {message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Az adatmappa nem hozható létre vagy nem írható: {dataDirectory.Path} ({ex.Message})");
            return null;
        }

        var serilog = LoggingSetup.CreateLogger(settings.LogsDir, consoleLevel);
        var loggerFactory = new SerilogLoggerFactory(serilog, dispose: false);
        var log = serilog.ForContext("SourceContext", "EfMailAgent");

        log.Information("EfMailAgent {Version}, adatmappa: {DataDir} ({Source})",
            VersionInfo.Current, dataDirectory.Path, dataDirectory.Source);
        if (report.SettingsCreated)
            log.Information("Első indítás: alapértelmezett beállítások létrehozva ({SettingsFile}), LLM szolgáltató: {Provider}",
                settings.SettingsFile, settings.Settings.Llm.Provider);
        else if (report.TokenGenerated)
            log.Warning("A beállításokban nem volt token, új token készült ({SettingsFile}).", settings.SettingsFile);
        if (report.SamplesCopiedFrom is not null)
            log.Information("A szabálymappa üres volt, a mintaszabályok átmásolva innen: {Source}", report.SamplesCopiedFrom);

        return new AgentContext(settings, serilog, loggerFactory, report);
    }
}
