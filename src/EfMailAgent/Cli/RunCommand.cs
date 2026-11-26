using System.Net.Sockets;
using EfMailAgent.Api;
using EfMailAgent.Llm;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EfMailAgent.Cli;

/// <summary>The long-running mode (no subcommand): worker + HTTP API until Ctrl+C or service stop.</summary>
internal static class RunCommand
{
    public static async Task<int> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var port = settings.Settings.Server.Port;
        await using var app = ApiHost.Build(settings, context.Serilog);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            context.Log.Fatal(
                "A(z) 127.0.0.1:{Port} port foglalt, az EfMailAgent nem tud elindulni. Valószínűleg már fut egy példány " +
                "(szolgáltatásként vagy egy másik ablakban), vagy egy másik program használja a portot. " +
                "Állítsd le a másik példányt, vagy módosítsd a Server.Port értékét itt: {SettingsFile}",
                port, settings.SettingsFile);
            return ExitCodes.PortInUse;
        }

        context.Log.Information("EfMailAgent fut: http://127.0.0.1:{Port}/api (LLM: {Provider}{Model}), adatmappa: {DataDir}. Leállítás: Ctrl+C.",
            port, settings.Settings.Llm.Provider,
            string.IsNullOrWhiteSpace(settings.Settings.Llm.Model) ? "" : ", " + settings.Settings.Llm.Model, settings.DataDirectory.Path);

        var health = await app.Services.GetRequiredService<ILlmClient>().CheckHealthAsync(cancellationToken);
        if (!health.Reachable)
            context.Log.Warning("Az LLM jelenleg nem érhető el: {Error}. A beérkező feladatok a sorban várnak.", health.Error);

        await app.WaitForShutdownAsync(cancellationToken);
        context.Log.Information("EfMailAgent leállt.");
        return ExitCodes.Ok;
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
            if (e is IOException && e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
