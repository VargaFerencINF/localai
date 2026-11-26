using EfMailAgent.Admin;
using EfMailAgent.Configuration;
using EfMailAgent.Hosting;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Tests.Fakes;
using EfMailAgent.Tests.Processing;

namespace EfMailAgent.Tests.Admin;

public class ModelAdvisorTests
{
    private static HardwareInfo Machine(double ramGb, GpuInfo? gpu = null) =>
        new(ramGb, "CPU", 16, gpu is null ? [] : [gpu], 100, "Windows");

    private static ModelAdvice Get(IReadOnlyList<ModelAdvice> advice, string name) => advice.Single(a => a.Name == name);

    [Fact]
    public void Built_in_catalog_loads_and_knows_successors_of_old_models()
    {
        var catalog = ModelCatalog.BuiltIn;
        Assert.Contains(catalog.Models, m => m.Name == "qwen3.5:9b" && m.Current);
        Assert.Equal("qwen3.5:9b", catalog.SuccessorOf("qwen3:8b"));
        Assert.Equal("qwen3.5:4b", catalog.SuccessorOf("qwen3:4b"));
        Assert.Equal("gemma4:e4b", catalog.SuccessorOf("gemma3:12b"));
        Assert.Equal("qwen3.5:9b", catalog.SuccessorOf("qwen2.5:7b-instruct"));
        Assert.Null(catalog.SuccessorOf("qwen3.5:9b"));
        Assert.Null(catalog.SuccessorOf("valami-ismeretlen:1b"));
    }

    [Fact]
    public void Laptop_with_32_gb_and_no_graphics_card_gets_the_9b_model_at_about_two_minutes()
    {
        var advice = ModelAdvisor.Advise(ModelCatalog.BuiltIn, Machine(32), new Dictionary<string, ModelSpeed>());

        var best = Get(advice, "qwen3.5:9b");
        Assert.Contains("ajanlott", best.Tags);
        Assert.Equal("ok", best.Fit);
        Assert.InRange(best.SecondsPerEmail!.Value, 90, 180);
        Assert.Contains("gyorsabb", Get(advice, "qwen3.5:4b").Tags);
        Assert.Equal("ajanlott", advice[0].Tags[0]); // the recommendation comes first
        Assert.NotEqual("no", Get(advice, "qwen3.5:27b").Fit); // fits in 32 GB ...
        Assert.DoesNotContain("ajanlott", Get(advice, "qwen3.5:27b").Tags); // ... but too slow on a processor
    }

    [Fact]
    public void Small_machine_gets_a_small_model_and_big_models_do_not_fit()
    {
        var advice = ModelAdvisor.Advise(ModelCatalog.BuiltIn, Machine(8), new Dictionary<string, ModelSpeed>());

        Assert.Equal("no", Get(advice, "qwen3.5:27b").Fit);
        Assert.Null(Get(advice, "qwen3.5:27b").SecondsPerEmail);
        var recommended = advice.Single(a => a.Tags.Contains("ajanlott"));
        Assert.Contains(recommended.Name, new[] { "qwen3.5:4b", "qwen3.5:2b" });
    }

    [Fact]
    public void Graphics_card_with_enough_memory_makes_big_models_fast()
    {
        var advice = ModelAdvisor.Advise(ModelCatalog.BuiltIn, Machine(64, new GpuInfo("NVIDIA GeForce RTX 4090", 24, true)), new Dictionary<string, ModelSpeed>());

        var big = Get(advice, "qwen3.5:27b");
        Assert.Contains("videókártyán", big.FitText);
        Assert.True(big.SecondsPerEmail < 60);
        Assert.Contains(advice.Single(a => a.Tags.Contains("ajanlott")).Name, new[] { "qwen3.5:27b", "gemma4:26b" });
    }

    [Fact]
    public void Measured_speed_replaces_the_estimate_and_calibrates_the_others()
    {
        var measured = new Dictionary<string, ModelSpeed> { ["qwen3.5:9b"] = new(4, 40, "teszt") };
        var advice = ModelAdvisor.Advise(ModelCatalog.BuiltIn, Machine(32), measured);

        var nine = Get(advice, "qwen3.5:9b");
        Assert.True(nine.Measured);
        Assert.Equal(3000 / 40 + 600 / 4, nine.SecondsPerEmail);
        // calibrated: half the default bandwidth -> the 4B estimate is slower than without measurement
        var uncalibrated = Get(ModelAdvisor.Advise(ModelCatalog.BuiltIn, Machine(32), new Dictionary<string, ModelSpeed>()), "qwen3.5:4b");
        Assert.True(Get(advice, "qwen3.5:4b").SecondsPerEmail > uncalibrated.SecondsPerEmail);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060", true)]
    [InlineData("AMD Radeon RX 7800 XT", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", false)]
    [InlineData("Intel(R) UHD Graphics 770", false)]
    [InlineData("AMD Radeon(TM) Graphics", false)]
    public void Discrete_and_integrated_graphics_are_told_apart(string name, bool discrete) =>
        Assert.Equal(discrete, SystemMetrics.IsDiscrete(name, null));
}

public class PerformanceTests
{
    [Fact]
    public void System_metrics_report_memory_cpu_and_hardware_on_this_machine()
    {
        using var metrics = new SystemMetrics();
        var hw = metrics.Hardware();
        Assert.True(hw.TotalRamGb > 0);
        Assert.True(hw.LogicalCores > 0);
        var memory = metrics.Memory();
        Assert.NotNull(memory);
        Assert.InRange(memory!.Value.AvailableGb, 0, memory.Value.TotalGb);
        metrics.CpuPercent();
        Thread.Sleep(200);
        var cpu = metrics.CpuPercent();
        if (cpu is not null)
            Assert.InRange(cpu.Value, 0, 100);
    }

    [Fact]
    public void Warnings_explain_overload_and_a_model_that_does_not_fit()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 30).Select(i => new PerfSample(now.AddSeconds(-2 * (30 - i)), 97, 30.5, 32, null)).ToList();
        var hw = new HardwareInfo(32, "CPU", 8, [new GpuInfo("NVIDIA GeForce RTX 3060", 12, true)], 100, "Windows");
        var ollama = new OllamaRuntimeState(true, [new LoadedModel("qwen3.5:27b", 30, 0), new LoadedModel("qwen3.5:9b", 7, 3)], now);
        var jobs = Enumerable.Range(0, 3).Select(i => new JobQueue.LlmUsageRow($"j{i}", "x", "qwen3.5:27b", 2, 3000, 600, 400_000, now, "done")).ToList();

        var warnings = PerfReport.Warnings(samples, ollama, hw, jobs);

        Assert.Contains(warnings, w => w.Contains("90% fölött"));
        Assert.Contains(warnings, w => w.Contains("Kevés a szabad memória"));
        Assert.Contains(warnings, w => w.Contains("túl nagy ehhez a géphez"));
        Assert.Contains(warnings, w => w.Contains("processzoron fut, pedig van videókártya"));
        Assert.Contains(warnings, w => w.Contains("csak részben fér el"));
        Assert.Contains(warnings, w => w.Contains("perc"));
        Assert.Empty(PerfReport.Warnings([], new OllamaRuntimeState(true, [], now), hw, []));
    }

    [Fact]
    public async Task Ollama_speed_and_tokens_are_saved_with_the_job()
    {
        await using var server = await FakeLlmServer.StartAsync();
        server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow);
        using var h = new PipelineHarness(s => { s.Llm.Provider = LlmProviders.Ollama; s.Llm.BaseUrl = server.BaseUrl; s.Llm.Model = "qwen3.5:9b"; });
        h.Enqueue();

        var (_, job) = await h.ProcessNextAsync();

        Assert.Equal(10, job.TokensPerSecond);
        Assert.Equal(100, job.PromptTokens);
        Assert.Equal(50, job.OutputTokens);
        Assert.Equal("qwen3.5:9b", job.LlmModel);
        var usage = Assert.Single(h.Queue.RecentLlmUsage(10));
        Assert.Equal(10, usage.TokensPerSecond);
    }

    [Fact]
    public void Usage_of_a_repaired_job_is_summed_and_the_speed_weighted()
    {
        var usage = LlmUsage.From(new LlmResponse("a", TimeSpan.FromSeconds(1), "m", 100, 50, 10), new LlmResponse("b", TimeSpan.FromSeconds(1), "m", 200, 150, 30));
        Assert.Equal(300, usage!.PromptTokens);
        Assert.Equal(200, usage.OutputTokens);
        Assert.Equal(20, usage.TokensPerSecond); // 200 tokens in 5 + 5 seconds
        Assert.Null(LlmUsage.From());
    }
}
