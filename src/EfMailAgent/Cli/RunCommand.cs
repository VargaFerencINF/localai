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
    public static async Task<int> RunAsync(AgentContext context, bool openBrowser, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var port = settings.Settings.Server.Port;

        // Checked before anything starts: the worker of a second instance must never touch the queue of the running one.
        if (await AdminLauncher.IsRunningAsync(settings, cancellationToken))
        {
            if (openBrowser)
            {
                // Started again while it already runs (as the service or in another window): just open the UI.
                context.Log.Information("Az EfMailAgent már fut (szolgáltatásként vagy egy másik ablakban), megnyitom a kezelőfelületet.");
                OpenUi(context, "attekintes");
                return ExitCodes.Ok;
            }
            return PortInUse(context);
        }
        if (!IsPortFree(port))
            return PortInUse(context);

        await using var app = ApiHost.Build(settings, context.Serilog);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            return PortInUse(context); // lost a race for the port after the check above
        }

        context.Log.Information("EfMailAgent fut: http://127.0.0.1:{Port}/api (LLM: {Provider}{Model}), adatmappa: {DataDir}. Leállítás: Ctrl+C.",
            port, settings.Settings.Llm.Provider,
            string.IsNullOrWhiteSpace(settings.Settings.Llm.Model) ? "" : ", " + settings.Settings.Llm.Model, settings.DataDirectory.Path);

        var health = await app.Services.GetRequiredService<ILlmClient>().CheckHealthAsync(cancellationToken);
        if (!health.Reachable)
            context.Log.Warning("Az LLM jelenleg nem érhető el: {Error}. A beérkező feladatok a sorban várnak. Az AI kapcsolat a kezelőfelület Beállítások oldalán adható meg.", health.Error);

        if (openBrowser)
        {
            // First start or no working AI: open the settings, otherwise the overview.
            var needsSetup = context.Report.SettingsCreated || !health.Reachable;
            OpenUi(context, needsSetup ? "beallitasok" : "attekintes");
            context.Log.Information("Ez az ablak az EfMailAgent konzolja: a bezárásával a program leáll. Folyamatos működéshez a telepítő szolgáltatásként telepíti.");
        }

        await app.WaitForShutdownAsync(cancellationToken);
        context.Log.Information("EfMailAgent leállt.");
        return ExitCodes.Ok;
    }

    private static int PortInUse(AgentContext context)
    {
        context.Log.Fatal(
            "A(z) 127.0.0.1:{Port} port foglalt, az EfMailAgent nem tud elindulni. Valószínűleg már fut egy példány " +
            "(szolgáltatásként vagy egy másik ablakban), vagy egy másik program használja a portot. " +
            "Állítsd le a másik példányt, vagy módosítsd a Server.Port értékét itt: {SettingsFile}",
            context.Settings.Settings.Server.Port, context.Settings.SettingsFile);
        return ExitCodes.PortInUse;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void OpenUi(AgentContext context, string page)
    {
        var error = AdminLauncher.TryOpen(AdminLauncher.LoginUrl(context.Settings, page));
        if (error is null)
            context.Log.Information("Kezelőfelület megnyitva a böngészőben: {Url}", AdminLauncher.PlainUrl(context.Settings));
        else
            context.Log.Warning("A böngésző nem nyitható meg ({Error}). A kezelőfelület címe: {Url} (token: EfMailAgent.exe print-token)",
                error, AdminLauncher.PlainUrl(context.Settings));
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
