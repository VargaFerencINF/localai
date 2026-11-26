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
        NullLogger<OllamaSetupService>.Instance);

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
        Assert.Contains(status["recommended"]!.GetValue<string>(), OllamaSetupService.Choices.Select(c => c.Name));
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

    [Theory]
    [InlineData(8, "qwen3:4b")]
    [InlineData(16, "qwen3:8b")]
    [InlineData(32, "qwen3:14b")]
    public void Recommendation_follows_the_memory(int ramGb, string expected) => Assert.Equal(expected, OllamaSetupService.Recommend(ramGb));

    [Fact]
    public async Task Pull_reports_progress_and_finishes_then_apply_switches_the_settings()
    {
        var service = Service();
        service.StartPull("qwen3:8b", _ollama.BaseUrl);
        var done = await WaitAsync(service);

        Assert.False(done.Failed, done.Message);
        Assert.Equal(100, done.Percent);
        Assert.Contains("qwen3:8b", _ollama.Models);

        var result = service.Apply("qwen3:8b", _ollama.BaseUrl);
        Assert.True(result.Saved, string.Join(" ", result.Errors));
        Assert.Equal(LlmProviders.Ollama, _settings.Settings.Llm.Provider);
        Assert.Equal("qwen3:8b", _settings.Settings.Llm.Model);
        Assert.Equal(_ollama.BaseUrl, _settings.Settings.Llm.BaseUrl);
        var file = JsonNode.Parse(File.ReadAllText(_settings.SettingsFile))!;
        Assert.Equal("qwen3:8b", file["Llm"]!["Model"]!.GetValue<string>());
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
}
