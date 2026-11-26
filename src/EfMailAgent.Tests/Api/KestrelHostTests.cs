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

    [Fact]
    public async Task Second_start_next_to_a_running_instance_does_not_start_a_worker()
    {
        using var tmp = new TempDir();
        var port = FreePort();
        var settings = Settings(tmp, port);
        tmp.WriteFile("appsettings.json", System.Text.Json.JsonSerializer.Serialize(settings.Settings));
        await using var app = ApiHost.Build(settings, new LoggerConfiguration().CreateLogger());
        await app.StartAsync();

        // Same data folder and token, like double-clicking the exe while the service runs (scripted: no browser).
        var code = await CliApp.RunAsync(["--data-dir", tmp.Path, "--no-browser"], new StringWriter(), new StringWriter());

        Assert.Equal(ExitCodes.PortInUse, code);
        var log = string.Concat(Directory.GetFiles(tmp.Combine("logs")).Select(File.ReadAllText));
        Assert.Contains("port foglalt", log);
        Assert.DoesNotContain("A feldolgozó elindult", log); // the second instance never touched the queue
        await app.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Starting_again_while_running_only_opens_the_ui_and_writes_nothing()
    {
        using var tmp = new TempDir();
        var port = FreePort();
        var settings = Settings(tmp, port);
        tmp.WriteFile("appsettings.json", System.Text.Json.JsonSerializer.Serialize(settings.Settings));
        await using var app = ApiHost.Build(settings, new LoggerConfiguration().CreateLogger());
        await app.StartAsync();
        // The running instance itself may touch its SQLite side files (-wal/-shm); only the second start must write nothing.
        string[] Files() => Directory.GetFiles(tmp.Path, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)).Order().ToArray();
        var filesBefore = Files();
        Environment.SetEnvironmentVariable(AdminLauncher.NoBrowserVariable, "1");
        try
        {
            var output = new StringWriter();
            Assert.True(await AdminLauncher.OpenRunningInstanceAsync(tmp.Path, output, CancellationToken.None));
            Assert.Contains("Az EfMailAgent már fut", output.ToString());
            Assert.Contains($"http://127.0.0.1:{port}/admin/", output.ToString());
            Assert.Equal(filesBefore, Files());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdminLauncher.NoBrowserVariable, null);
        }

        await app.StopAsync();
        Assert.False(await AdminLauncher.OpenRunningInstanceAsync(tmp.Path, new StringWriter(), CancellationToken.None)); // not running: start normally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Read_only_settings_of_a_missing_data_folder_create_nothing()
    {
        using var tmp = new TempDir();
        Assert.Null(AdminLauncher.LoadReadOnly(tmp.Combine("nincs")));
        Assert.False(Directory.Exists(tmp.Combine("nincs")));
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
