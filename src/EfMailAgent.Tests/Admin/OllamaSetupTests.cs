using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Admin;
using EfMailAgent.Configuration;
using EfMailAgent.Api;
using EfMailAgent.Hosting;
using EfMailAgent.Llm;
using EfMailAgent.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfMailAgent.Tests.Admin;

public class OllamaSetupTests : IAsyncLifetime
{
    private FakeLlmServer _ollama = null!;
    private TempDir _tmp = null!;
    private EffectiveSettings _settings = null!;
    private readonly FakePlatform _platform = new();

    public async Task InitializeAsync()
    {
        _ollama = await FakeLlmServer.StartAsync();
        _tmp = new TempDir();
        var app = new AppSettings();
        app.Server.Token = TokenGenerator.NewToken();
        app.Llm.Provider = LlmProviders.Mock;
        app.Llm.MockDir = Path.Combine(TestPaths.SamplesRoot, "mock-responses");
        _settings = new EffectiveSettings(new DataDirectory(_tmp.Path, DataDirectorySource.CommandLine), app);
        Directory.CreateDirectory(_settings.OutputDir);
        SettingsBootstrapper.WriteAtomic(_settings.SettingsFile, JsonSerializer.Serialize(app));
    }

    public async Task DisposeAsync()
    {
        await _ollama.DisposeAsync();
        _tmp.Dispose();
    }

    private OllamaSetupService Service(bool isService = false, bool installSupported = true) => new(
        _settings, new SettingsService(_settings, new LlmHealthCache(new ReloadableLlmClient(_settings), TimeProvider.System)),
        new RuntimeInfo { IsWindowsService = isService }, _platform,
        new OllamaSetupOptions { DownloadUrl = $"{_ollama.BaseUrl}/download/OllamaSetup.exe", StartWait = TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(50), InstallSupported = installSupported },
        NullLogger<OllamaSetupService>.Instance, new FixedMetrics(), new EfMailAgent.Jobs.JobQueue(new EfMailAgent.Jobs.JobDatabase(_settings.DataDir), TimeProvider.System));

    private static async Task<OllamaTaskState> WaitAsync(OllamaSetupService service)
    {
        for (var i = 0; i < 200 && service.CurrentTask.Running; i++)
            await Task.Delay(50);
        Assert.False(service.CurrentTask.Running, "the task did not finish");
        return service.CurrentTask;
    }

    private static JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();

    [Fact]
    public async Task Status_shows_a_running_ollama_with_its_models_and_a_recommendation()
    {
        var status = Json(await Service().StatusAsync(_ollama.BaseUrl, CancellationToken.None));

        Assert.True(status["reachable"]!.GetValue<bool>());
        Assert.Equal("0.12.3", status["version"]!.GetValue<string>());
        Assert.Contains("qwen3.5:9b", status["models"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()));
        Assert.Equal("qwen3.5:9b", status["recommended"]!.GetValue<string>()); // 32 GB, no graphics card
        Assert.Equal(32, status["ramGb"]!.GetValue<int>());
        Assert.NotEmpty(status["advice"]!.AsArray());
        Assert.True(status["canInstall"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Status_of_an_unreachable_ollama_and_install_refused_when_running_as_service()
    {
        var status = Json(await Service(isService: true).StatusAsync("http://127.0.0.1:9", CancellationToken.None));

        Assert.False(status["reachable"]!.GetValue<bool>());
        Assert.False(status["canInstall"]!.GetValue<bool>());
        Assert.Contains("szolgáltatásként", status["cannotInstallReason"]!.GetValue<string>());
        var ex = Assert.Throws<AdminRequestException>(() => Service(isService: true).StartInstall());
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task Pull_reports_progress_and_finishes_then_apply_switches_the_settings()
    {
        var service = Service();
        service.StartPull("qwen3.5:9b", _ollama.BaseUrl);
        var done = await WaitAsync(service);

        Assert.False(done.Failed, done.Message);
        Assert.Equal(100, done.Percent);
        Assert.Contains("qwen3.5:9b", _ollama.Models);

        var result = service.Apply("qwen3.5:9b", _ollama.BaseUrl);
        Assert.True(result.Saved, string.Join(" ", result.Errors));
        Assert.Equal(LlmProviders.Ollama, _settings.Settings.Llm.Provider);
        Assert.Equal("qwen3.5:9b", _settings.Settings.Llm.Model);
        Assert.Equal(_ollama.BaseUrl, _settings.Settings.Llm.BaseUrl);
        var file = JsonNode.Parse(File.ReadAllText(_settings.SettingsFile))!;
        Assert.Equal("qwen3.5:9b", file["Llm"]!["Model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_model_gives_a_hungarian_hint_and_bad_names_are_refused()
    {
        _ollama.PullLines = _ => ["""{"status":"pulling manifest"}""", """{"error":"pull model manifest: file does not exist"}"""];
        var service = Service();
        service.StartPull("nincsilyen:1b", _ollama.BaseUrl);
        var done = await WaitAsync(service);

        Assert.True(done.Failed);
        Assert.Contains("Nincs ilyen modell", done.Message);
        Assert.Throws<AdminRequestException>(() => service.StartPull("rossz név; rm -rf", _ollama.BaseUrl));
        Assert.Throws<AdminRequestException>(() => service.Apply("", _ollama.BaseUrl));
    }

    [Fact]
    public async Task Install_downloads_checks_the_signature_runs_the_installer_and_waits_for_ollama()
    {
        var service = Service();
        service.StartInstall();
        var done = await WaitAsync(service);

        Assert.Equal(_ollama.SetupBytes.Length, _platform.VerifiedSize);
        Assert.Equal(1, _platform.Runs);
        // Nothing listens on 127.0.0.1:11434 in the test: it tried to start the Ollama app and then explains what to do.
        Assert.True(done.Failed);
        Assert.Contains("nem indult el", done.Message);
    }

    [Fact]
    public async Task Install_stops_before_running_an_installer_with_a_bad_signature()
    {
        _platform.SignatureError = "a digitális aláírás nem érvényes (NotSigned|)";
        var service = Service();
        service.StartInstall();
        var done = await WaitAsync(service);

        Assert.True(done.Failed);
        Assert.Contains("nem indítottam el", done.Message);
        Assert.Equal(0, _platform.Runs);
    }

    private sealed class FakePlatform : IOllamaPlatform
    {
        public string? SignatureError { get; set; }
        public long VerifiedSize { get; private set; }
        public int Runs { get; private set; }
        public Action OnRun { get; set; } = () => { };

        public Task<string?> VerifySignatureAsync(string file, CancellationToken cancellationToken)
        {
            VerifiedSize = new FileInfo(file).Length;
            return Task.FromResult(SignatureError);
        }

        public Task<int> RunInstallerAsync(string file, CancellationToken cancellationToken)
        {
            Runs++;
            OnRun();
            return Task.FromResult(0);
        }

        public void TryStartOllama() { }

        public bool IsInstalled() => false;
    }

    [Fact]
    public async Task Models_can_be_deleted_without_updating_and_the_used_one_only_with_a_switch()
    {
        _ollama.Models = ["qwen3:8b", "qwen3.5:9b", "llama3.2:3b"];
        var service = Service();
        service.Apply("qwen3:8b", _ollama.BaseUrl);

        var status = Json(await service.StatusAsync(_ollama.BaseUrl, CancellationToken.None));
        var old = status["models"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "qwen3:8b")!;
        Assert.Equal("qwen3.5:9b", old["successor"]!.GetValue<string>());
        Assert.True(old["inUse"]!.GetValue<bool>());
        Assert.True(old["successorInstalled"]!.GetValue<bool>());

        // plain delete of an outdated model that is not in use: no update needed
        var plain = await service.DeleteAsync(["llama3.2:3b"], null, _ollama.BaseUrl, CancellationToken.None);
        Assert.Equal(["llama3.2:3b"], plain.Deleted);
        Assert.Equal(5.0, plain.FreedGb);

        // the model in use: refused without a switch target, deleted after switching
        var refused = await Assert.ThrowsAsync<AdminRequestException>(() => service.DeleteAsync(["qwen3:8b"], null, _ollama.BaseUrl, CancellationToken.None));
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("qwen3:8b", _ollama.Models);
        var switched = await service.DeleteAsync(["qwen3:8b"], "qwen3.5:9b", _ollama.BaseUrl, CancellationToken.None);
        Assert.Equal("qwen3.5:9b", switched.SwitchedTo);
        Assert.Equal("qwen3.5:9b", _settings.Settings.Llm.Model);
        Assert.Equal(["qwen3.5:9b"], _ollama.Models);
    }

    [Fact]
    public async Task Bulk_delete_reports_each_model()
    {
        _ollama.Models = ["a:1b", "b:2b", "c:3b"];
        var result = await Service().DeleteAsync(["a:1b", "b:2b", "nincs:1b"], null, _ollama.BaseUrl, CancellationToken.None);

        Assert.Equal(["a:1b", "b:2b"], result.Deleted);
        Assert.Single(result.Errors);
        Assert.Contains("nincs:1b", result.Errors[0]);
        Assert.Equal(["c:3b"], _ollama.Models);
        await Assert.ThrowsAsync<AdminRequestException>(() => Service().DeleteAsync([], null, _ollama.BaseUrl, CancellationToken.None));
    }

    [Fact]
    public async Task Quick_test_measures_the_speed_saves_it_and_the_advice_uses_it()
    {
        var service = Service();
        var json = Json(await service.BenchmarkAsync("qwen3.5:9b", _ollama.BaseUrl, CancellationToken.None));

        Assert.Equal(10, json["generationTokPerSec"]!.GetValue<double>());
        Assert.Equal(100, json["promptTokPerSec"]!.GetValue<double>());
        Assert.Equal(3000 / 100 + 600 / 10, json["secondsPerEmail"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(_settings.DataDir, OllamaSetupService.BenchmarkFileName)));

        var status = Json(await service.StatusAsync(_ollama.BaseUrl, CancellationToken.None));
        var advice = status["advice"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "qwen3.5:9b")!;
        Assert.True(advice["measured"]!.GetValue<bool>());
        Assert.Equal(90, advice["secondsPerEmail"]!.GetValue<int>());
    }

    private sealed class FixedMetrics : EfMailAgent.Hosting.ISystemMetrics
    {
        public EfMailAgent.Hosting.HardwareInfo Hardware() => new(32, "Intel(R) Core(TM) Ultra 7 155H", 22, [new("Intel(R) Arc(TM) Graphics", 0.1, false)], 200, "Windows 11");
        public (double TotalGb, double AvailableGb)? Memory() => (32, 16);
        public double? CpuPercent() => 10;
        public double? GpuPercent() => null;
    }
}
