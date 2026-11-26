using System.Net.Http.Json;
using EfMailAgent.Api;
using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EfMailAgent.Tests.Api;

/// <summary>The real API host (same composition as production) on an in-memory TestServer, with the mock LLM.</summary>
public sealed class ApiFixture : IAsyncDisposable
{
    private ApiFixture(TempDir temp, EffectiveSettings settings, WebApplication app)
    {
        Temp = temp;
        Settings = settings;
        App = app;
        Client = app.GetTestClient();
        Client.DefaultRequestHeaders.Add(ApiGuardMiddleware.TokenHeader, settings.Settings.Server.Token);
    }

    public TempDir Temp { get; }
    public EffectiveSettings Settings { get; }
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public JobQueue Queue => App.Services.GetRequiredService<JobQueue>();

    public static async Task<ApiFixture> StartAsync(Action<AppSettings>? configure = null)
    {
        var temp = new TempDir();
        var appSettings = new AppSettings();
        appSettings.Server.Token = TokenGenerator.NewToken();
        appSettings.Llm.Provider = LlmProviders.Mock;
        appSettings.Llm.MockDir = Path.Combine(TestPaths.SamplesRoot, "mock-responses");
        appSettings.Llm.MockDelayMs = 0;
        appSettings.Worker.IdlePollMilliseconds = 100;
        configure?.Invoke(appSettings);
        var settings = new EffectiveSettings(new DataDirectory(temp.Path, DataDirectorySource.CommandLine), appSettings);
        SettingsBootstrapper.CopyDirectory(TestPaths.SampleRules, settings.RulesDir);

        var app = ApiHost.Build(settings, new LoggerConfiguration().CreateLogger(), builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new ApiFixture(temp, settings, app);
    }

    public Task<HttpResponseMessage> SubmitAsync(object payload) => Client.PostAsJsonAsync("/api/jobs", payload);

    public static object Payload(string messageId = "<api-1@x.example>", string subject = "Fwd: Előrendelés tavaszra",
        string? bodyText = "Kérünk 10 db feeder botot.", string? bodyHtml = null, string? ruleId = null, string source = "auto") => new
    {
        messageId,
        account = "eloreendeles@x.example",
        folder = "Inbox",
        receivedAt = "2026-11-25T08:41:00.000Z",
        from = "Kovács Péter <kovacs.peter@ertekesites.example>",
        to = new[] { "eloreendeles@x.example", "masik@x.example" },
        subject,
        bodyText,
        bodyHtml,
        attachmentNames = new[] { "rendeles.xlsx" },
        source,
        ruleId,
    };

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Temp.Dispose();
    }
}
