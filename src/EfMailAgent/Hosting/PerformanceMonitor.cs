using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Configuration;
using Microsoft.Extensions.Hosting;

namespace EfMailAgent.Hosting;

public sealed record PerfSample(DateTimeOffset Time, double? Cpu, double? MemoryUsedGb, double? MemoryTotalGb, double? Gpu);

public sealed record LoadedModel(string Name, double SizeGb, double VramGb)
{
    /// <summary>"gpu", "cpu" or "vegyes" (partly offloaded).</summary>
    public string Placement => VramGb <= 0.05 ? "cpu" : VramGb >= SizeGb * 0.95 ? "gpu" : "vegyes";
}

public sealed record OllamaRuntimeState(bool Reachable, IReadOnlyList<LoadedModel> Loaded, DateTimeOffset CheckedAt);

/// <summary>
/// Samples processor, memory and graphics card load every 2 seconds (15 minutes kept) and asks Ollama which model is
/// loaded every 5 seconds, for the Teljesítmény page. Light: a few system calls per tick.
/// </summary>
public sealed class PerformanceMonitor(ISystemMetrics metrics, EffectiveSettings settings, TimeProvider time) : BackgroundService
{
    public const int Capacity = 450;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly LinkedList<PerfSample> _samples = new();
    private OllamaRuntimeState _ollama = new(false, [], DateTimeOffset.MinValue);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public IReadOnlyList<PerfSample> Samples()
    {
        lock (_lock)
            return _samples.ToList();
    }

    public OllamaRuntimeState Ollama => _ollama;

    public void Sample()
    {
        var memory = metrics.Memory();
        var sample = new PerfSample(time.GetUtcNow(), metrics.CpuPercent(),
            memory is { } m ? Math.Round(m.TotalGb - m.AvailableGb, 2) : null, memory?.TotalGb is { } t ? Math.Round(t, 2) : null,
            metrics.GpuPercent());
        lock (_lock)
        {
            _samples.AddLast(sample);
            while (_samples.Count > Capacity)
                _samples.RemoveFirst();
        }
    }

    public async Task RefreshOllamaAsync(CancellationToken cancellationToken)
    {
        var llm = settings.Settings.Llm;
        if (llm.Provider != LlmProviders.Ollama)
        {
            _ollama = new OllamaRuntimeState(false, [], time.GetUtcNow());
            return;
        }
        try
        {
            var ps = await _http.GetFromJsonAsync<JsonObject>($"{llm.BaseUrl.TrimEnd('/')}/api/ps", cancellationToken);
            var loaded = (ps?["models"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new LoadedModel(
                m["name"]?.GetValue<string>() ?? m["model"]?.GetValue<string>() ?? "?",
                Math.Round((m["size"]?.GetValue<long>() ?? 0) / 1e9, 1),
                Math.Round((m["size_vram"]?.GetValue<long>() ?? 0) / 1e9, 1))).ToList();
            _ollama = new OllamaRuntimeState(true, loaded, time.GetUtcNow());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _ollama = new OllamaRuntimeState(false, [], time.GetUtcNow());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            Sample();
            if (tick++ % 3 == 0)
                await RefreshOllamaAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
