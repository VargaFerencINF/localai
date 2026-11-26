using System.Net;
using System.Net.Sockets;
using EfMailAgent.Api;
using EfMailAgent.Cli;
using EfMailAgent.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EfMailAgent.Tests.Api;

/// <summary>Tests with the real Kestrel server (binding, port conflicts).</summary>
[Collection("Console")]
public class KestrelHostTests
{
    [Fact]
    public async Task Api_listens_on_loopback_only()
    {
        using var tmp = new TempDir();
        var port = FreePort();
        var settings = Settings(tmp, port);
        await using var app = ApiHost.Build(settings, new LoggerConfiguration().CreateLogger());
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.Equal([$"http://127.0.0.1:{port}"], addresses);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add(ApiGuardMiddleware.TokenHeader, settings.Settings.Server.Token);
        var health = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await app.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Taken_port_stops_with_a_hungarian_message_and_exit_code()
    {
        using var tmp = new TempDir();
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        EfMailAgent.Configuration.SettingsBootstrapper.CopyDirectory(TestPaths.SampleRules, tmp.Combine("data", "rules"));
        tmp.WriteFile("data/appsettings.json", $$"""
            {
              "Server": { "Port": {{port}}, "Token": "{{TokenGenerator.NewToken()}}" },
              "Llm": { "Provider": "mock", "MockDir": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(TestPaths.SamplesRoot, "mock-responses"))}} }
            }
            """);

        var code = await CliApp.RunAsync(["--data-dir", tmp.Combine("data")], new StringWriter(), new StringWriter());

        Assert.Equal(ExitCodes.PortInUse, code);
        var log = File.ReadAllText(Directory.GetFiles(tmp.Combine("data", "logs")).Single());
        Assert.Contains($"127.0.0.1:{port} port foglalt", log);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static EffectiveSettings Settings(TempDir tmp, int port)
    {
        var appSettings = new AppSettings();
        appSettings.Server.Port = port;
        appSettings.Server.Token = TokenGenerator.NewToken();
        appSettings.Llm.Provider = LlmProviders.Mock;
        appSettings.Llm.MockDir = Path.Combine(TestPaths.SamplesRoot, "mock-responses");
        var settings = new EffectiveSettings(new DataDirectory(tmp.Path, DataDirectorySource.CommandLine), appSettings);
        SettingsBootstrapper.CopyDirectory(TestPaths.SampleRules, settings.RulesDir);
        return settings;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
