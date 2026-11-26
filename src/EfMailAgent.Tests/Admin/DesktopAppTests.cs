using System.Net;
using EfMailAgent.Addon;
using EfMailAgent.Admin;
using EfMailAgent.Cli;
using EfMailAgent.Tests.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfMailAgent.Tests.Admin;

/// <summary>v1.2.0: app window, quit, Thunderbird add-on install from the UI.</summary>
public class DesktopAppTests : IAsyncLifetime
{
    private ApiFixture _api = null!;
    private AdminTestClient _admin = null!;

    public async Task InitializeAsync()
    {
        _api = await ApiFixture.StartAsync();
        _admin = new AdminTestClient(_api);
        await _admin.LoginAsync();
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public void App_window_prefers_edge_then_chrome_with_its_own_profile()
    {
        var env = new Dictionary<string, string?> { ["ProgramFiles(x86)"] = @"C:\PF86", ["ProgramFiles"] = @"C:\PF", ["LOCALAPPDATA"] = @"C:\LA" };
        string Find(params string[] existing) => AppWindow.FindBrowser(f => existing.Contains(f.Replace('/', '\\')), k => env.GetValueOrDefault(k)) ?? "";

        Assert.EndsWith(@"Edge\Application\msedge.exe", Find(@"C:\PF86\Microsoft\Edge\Application\msedge.exe", @"C:\PF\Google\Chrome\Application\chrome.exe").Replace('/', '\\'));
        Assert.EndsWith(@"Chrome\Application\chrome.exe", Find(@"C:\LA\Google\Chrome\Application\chrome.exe").Replace('/', '\\'));
        Assert.Equal("", Find());

        var args = AppWindow.Arguments("http://127.0.0.1:47811/admin/login?code=x", @"C:\LA\EfMailAgent\app-window");
        Assert.Contains("--app=http://127.0.0.1:47811/admin/login?code=x", args);
        Assert.Contains(@"--user-data-dir=C:\LA\EfMailAgent\app-window", args);
    }

    [Fact]
    public void Start_failure_text_is_hungarian_and_points_to_the_logs()
    {
        Assert.Contains("másik program használja", CliApp.StartFailureText(ExitCodes.PortInUse, ""));
        Assert.Contains("Hibás beállítások", CliApp.StartFailureText(ExitCodes.ConfigurationError, "Hibás beállítások (x)"));
        Assert.Contains("logs", CliApp.StartFailureText(99, ""));
    }

    [Fact]
    public async Task Quit_stops_the_program_when_it_is_not_the_service()
    {
        Assert.True((await _admin.GetJsonAsync("/admin/api/session"))["canQuit"]!.GetValue<bool>());
        var stopping = _api.App.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        var (status, _) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/quit");

        Assert.Equal(200, status);
        await Task.Delay(1500);
        Assert.True(stopping.IsCancellationRequested);
    }

    [Fact]
    public async Task Quit_needs_a_session_and_the_csrf_header()
    {
        var anonymous = new AdminTestClient(_api);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/admin/api/quit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.SendAsync(HttpMethod.Post, "/admin/api/quit", csrfHeader: false, origin: "http://evil.example")).StatusCode);
    }

    [Fact]
    public async Task Addon_status_and_install_without_thunderbird_explain_what_to_do()
    {
        var status = await _admin.GetJsonAsync("/admin/api/addon");
        Assert.False(status["ok"]!.GetValue<bool>());

        var (code, body) = await _admin.JsonAsync(HttpMethod.Post, "/admin/api/addon/install");
        Assert.Equal(422, code);
        // No Thunderbird here, and depending on the build order the XPI may not be packaged yet: either is explained.
        var messages = body!["messages"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(messages.Contains("Thunderbird") || messages.Contains("add-on csomag"), messages);
        Assert.False(body["ok"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(0, true, "Indítsd újra a Thunderbirdöt")]
    [InlineData(null, false, "engedélykérést elutasították")]
    [InlineData(8, false, "policies.json")]
    [InlineData(6, false, "nem található")]
    public async Task Without_admin_rights_the_install_runs_elevated_and_reports_the_outcome(int? exitCode, bool ok, string expected)
    {
        var runner = new FakeRunner(exitCode);
        var service = new AddonSetupService(_api.Settings, new NotElevated(), runner, NullLogger<AddonSetupService>.Instance, isWindows: () => true);

        var result = await service.InstallAsync(CancellationToken.None);

        Assert.Equal(ok, result.Ok);
        Assert.Contains(expected, string.Join(" ", result.Messages));
        Assert.Equal(["install-addon", "--data-dir", _api.Settings.DataDirectory.Path], runner.Arguments);
    }

    private sealed class NotElevated : IElevationCheck
    {
        public bool IsElevated() => false;
    }

    private sealed class FakeRunner(int? exitCode) : IElevatedRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Arguments = arguments;
            return Task.FromResult(exitCode);
        }
    }
}
