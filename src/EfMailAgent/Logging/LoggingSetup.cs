using Serilog;
using Serilog.Events;

namespace EfMailAgent.Logging;

public static class LoggingSetup
{
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Console logging goes to stderr so that command output on stdout (e.g. print-token) stays machine-readable.
    /// </summary>
    public static ILogger CreateBootstrapLogger(LogEventLevel minimumLevel = LogEventLevel.Information) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.Console(outputTemplate: OutputTemplate, standardErrorFromLevel: LogEventLevel.Verbose)
            .CreateLogger();

    public static ILogger CreateLogger(string logsDir, LogEventLevel consoleLevel = LogEventLevel.Information,
        LogEventLevel fileLevel = LogEventLevel.Information)
    {
        Directory.CreateDirectory(logsDir);
        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate, restrictedToMinimumLevel: consoleLevel,
                standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.File(Path.Combine(logsDir, "efmailagent-.log"),
                restrictedToMinimumLevel: fileLevel,
                outputTemplate: OutputTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .CreateLogger();
    }
}
