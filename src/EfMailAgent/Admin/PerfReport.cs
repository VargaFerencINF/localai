using EfMailAgent.Configuration;
using EfMailAgent.Hosting;
using EfMailAgent.Jobs;

namespace EfMailAgent.Admin;

/// <summary>The data of the Teljesítmény page, with Hungarian warnings when the machine or the model is the bottleneck.</summary>
public static class PerfReport
{
    public static object Build(PerformanceMonitor monitor, HardwareInfo hardware, IReadOnlyList<JobQueue.LlmUsageRow> jobs, EffectiveSettings settings)
    {
        var samples = monitor.Samples();
        var ollama = monitor.Ollama;
        var llm = settings.Settings.Llm;
        var finished = jobs.Where(j => j.DurationMs is > 0).ToList();
        var today = DateTimeOffset.UtcNow.Date;
        var todayJobs = finished.Where(j => j.FinishedAt?.UtcDateTime.Date == today).ToList();
        var withSpeed = finished.Where(j => j.TokensPerSecond is > 0).ToList();

        return new
        {
            hardware,
            provider = llm.Provider,
            model = llm.Model,
            samples = samples.Select(s => new { t = s.Time, cpu = s.Cpu, mem = s.MemoryUsedGb, memTotal = s.MemoryTotalGb, gpu = s.Gpu }),
            current = samples.LastOrDefault(),
            gpuMeasurable = samples.Any(s => s.Gpu is not null),
            ollama = new
            {
                ollama.Reachable,
                loaded = ollama.Loaded.Select(m => new { m.Name, m.SizeGb, m.VramGb, m.Placement }),
            },
            jobs = finished.Take(15).Select(j => new
            {
                j.Id, j.Subject, j.Model, j.TokensPerSecond, j.PromptTokens, j.OutputTokens,
                seconds = Math.Round(j.DurationMs!.Value / 1000d, 1), j.FinishedAt, j.Status,
            }),
            stats = new
            {
                todayCount = todayJobs.Count,
                todayAvgSeconds = todayJobs.Count > 0 ? Math.Round(todayJobs.Average(j => j.DurationMs!.Value) / 1000d, 1) : (double?)null,
                avgTokensPerSecond = withSpeed.Count > 0 ? Math.Round(withSpeed.Average(j => j.TokensPerSecond!.Value), 1) : (double?)null,
                slowest = finished.OrderByDescending(j => j.DurationMs).Select(j => new { j.Subject, seconds = Math.Round(j.DurationMs!.Value / 1000d, 1) }).FirstOrDefault(),
            },
            warnings = Warnings(samples, ollama, hardware, finished),
        };
    }

    public static IReadOnlyList<string> Warnings(IReadOnlyList<PerfSample> samples, OllamaRuntimeState ollama, HardwareInfo hardware,
        IReadOnlyList<JobQueue.LlmUsageRow> finished)
    {
        var warnings = new List<string>();
        var lastMinute = samples.TakeLast(30).Where(s => s.Cpu is not null).ToList();
        if (lastMinute.Count >= 10 && lastMinute.Average(s => s.Cpu!.Value) > 90)
            warnings.Add("A processzor az elmúlt percben tartósan 90% fölött dolgozott. Ha közben más program is lassú, válassz kisebb modellt, vagy ütemezd a feldolgozást csendesebb időszakra.");
        if (samples.LastOrDefault() is { MemoryUsedGb: { } used, MemoryTotalGb: { } total } && total > 0 && (total - used) / total < 0.1)
            warnings.Add($"Kevés a szabad memória ({total - used:0.#} GB a {total:0} GB-ból). A modell lassulhat vagy nem fér el; zárj be más programokat, vagy válassz kisebb modellt.");
        foreach (var model in ollama.Loaded)
        {
            if (model.SizeGb > hardware.TotalRamGb * 0.85)
                warnings.Add($"A betöltött modell ({model.Name}, {model.SizeGb:0.#} GB) túl nagy ehhez a géphez ({hardware.TotalRamGb:0} GB memória). Válassz kisebbet a Beállításokban.");
            if (hardware.BestDiscreteGpu is { } gpu && model.Placement == "cpu")
                warnings.Add($"A modell ({model.Name}) processzoron fut, pedig van videókártya ({gpu.Name}). Ellenőrizd az Ollama videókártya-támogatását (illesztőprogram, Ollama verzió).");
            if (model.Placement == "vegyes")
                warnings.Add($"A modell ({model.Name}) csak részben fér el a videókártyán ({model.VramGb:0.#} / {model.SizeGb:0.#} GB), ezért lassabb. Egy kisebb modell teljesen elférne rajta.");
        }
        var recent = finished.Take(5).ToList();
        if (recent.Count >= 3 && recent.Average(j => j.DurationMs!.Value) > 300_000)
            warnings.Add($"Egy levél feldolgozása az utóbbi időben átlagosan {recent.Average(j => j.DurationMs!.Value) / 60000d:0.#} perc. Ha ez sok, a Beállításokban a „gyorsabb” jelölésű modellt érdemes kipróbálni.");
        return warnings;
    }
}
