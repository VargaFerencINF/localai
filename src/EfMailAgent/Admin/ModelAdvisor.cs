using System.Text.Json;
using System.Text.RegularExpressions;
using EfMailAgent.Hosting;

namespace EfMailAgent.Admin;

public sealed record CatalogModel(string Name, string Title, string Family, double ParamsB, double DownloadGb, int Hungarian, bool Current, string Description);

public sealed record CatalogSuccessor(string Pattern, string Successor);

/// <summary>The model catalog shipped with the program (Admin/ollama-models.json, updated with every release).</summary>
public sealed record ModelCatalog(string Updated, IReadOnlyList<CatalogModel> Models, IReadOnlyList<CatalogSuccessor> Successors)
{
    private static readonly Lazy<ModelCatalog> BuiltInCatalog = new(LoadBuiltIn);

    public static ModelCatalog BuiltIn => BuiltInCatalog.Value;

    public static ModelCatalog Parse(string json) =>
        JsonSerializer.Deserialize<ModelCatalog>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new JsonException("Üres modellkatalógus.");

    private static ModelCatalog LoadBuiltIn()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("ollama-models.json")
                           ?? throw new InvalidOperationException("A beépített modellkatalógus hiányzik.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public CatalogModel? Find(string name) => Models.FirstOrDefault(m => m.Name.Equals(Normalize(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>The newer model replacing an installed one, or null if it is current / unknown.</summary>
    public string? SuccessorOf(string installed)
    {
        var name = Normalize(installed);
        if (Find(name) is not null)
            return null;
        foreach (var rule in Successors)
        {
            var pattern = "^" + Regex.Escape(rule.Pattern).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase) && !rule.Successor.Equals(name, StringComparison.OrdinalIgnoreCase))
                return rule.Successor;
        }
        return null;
    }

    public static string Normalize(string name) => name.Trim();
}

/// <summary>Measured speed of a model on this machine (quick test or real jobs): generation and prompt tokens per second.</summary>
public sealed record ModelSpeed(double GenerationTokPerSec, double? PromptTokPerSec, string Source);

public sealed record ModelAdvice(
    string Name, string Title, string Description, double DownloadGb, int Hungarian, bool Current,
    string Fit, string FitText, int? SecondsPerEmail, bool Measured, IReadOnlyList<string> Tags);

/// <summary>
/// Ranks the catalog for this machine: does the model fit in memory (or in the graphics card), how long one email
/// would take, Hungarian quality and generation. Speeds are estimated from the memory bandwidth (CPU ~50 GB/s,
/// dedicated GPU ~250 GB/s effective) and replaced by measured values when a quick test or real jobs exist.
/// </summary>
public static class ModelAdvisor
{
    /// <summary>A typical pre-order email: assembled prompt and JSON answer in tokens.</summary>
    public const int PromptTokens = 3000;
    public const int OutputTokens = 600;
    public const int AcceptableSeconds = 240;

    public static IReadOnlyList<ModelAdvice> Advise(ModelCatalog catalog, HardwareInfo hw, IReadOnlyDictionary<string, ModelSpeed> measured)
    {
        var gpu = hw.BestDiscreteGpu;
        // Effective bandwidth calibrated from any measured model: tokens/s * model size stays roughly constant on one machine.
        var calibrated = measured
            .Select(m => (catalog.Find(m.Key)?.DownloadGb, m.Value.GenerationTokPerSec))
            .Where(x => x.DownloadGb is > 0)
            .Select(x => x.GenerationTokPerSec * x.DownloadGb!.Value)
            .Order().ToList();
        double? cpuBandwidth = calibrated.Count > 0 ? calibrated[calibrated.Count / 2] : null;

        var advice = new List<ModelAdvice>();
        foreach (var model in catalog.Models)
        {
            var need = model.DownloadGb * 1.2 + 1;
            var onGpu = gpu?.DedicatedVramGb is { } vram && need <= vram;
            string fit, fitText;
            if (onGpu)
                (fit, fitText) = ("ok", $"elfér a videókártyán ({gpu!.Name})");
            else if (need <= hw.TotalRamGb * 0.6)
                (fit, fitText) = ("ok", $"elfér a memóriában (kb. {need:0.#} GB / {hw.TotalRamGb:0} GB)");
            else if (need <= hw.TotalRamGb * 0.85)
                (fit, fitText) = ("tight", $"szűkösen fér el (kb. {need:0.#} GB / {hw.TotalRamGb:0} GB); más program mellett lassulhat");
            else
                (fit, fitText) = ("no", $"nem fér el (kb. {need:0.#} GB kellene, a gépben {hw.TotalRamGb:0} GB van)");

            int? seconds = null;
            var isMeasured = measured.TryGetValue(model.Name, out var speed);
            if (fit != "no")
            {
                double gen, prompt;
                if (isMeasured)
                {
                    gen = speed!.GenerationTokPerSec;
                    prompt = speed.PromptTokPerSec ?? gen * 12;
                }
                else
                {
                    var bandwidth = onGpu ? 250 : cpuBandwidth ?? 50;
                    gen = bandwidth / model.DownloadGb;
                    prompt = gen * (onGpu ? 30 : 12);
                }
                seconds = gen > 0 ? (int)Math.Round(PromptTokens / prompt + OutputTokens / gen) : null;
            }
            advice.Add(new ModelAdvice(model.Name, model.Title, model.Description, model.DownloadGb, model.Hungarian, model.Current,
                fit, fitText, seconds, isMeasured, []));
        }

        // Tags: ajánlott (best Hungarian within ~4 minutes per email), gyorsabb, pontosabb.
        var usable = advice.Where(a => a.Fit != "no" && a.SecondsPerEmail is not null).ToList();
        var tags = advice.ToDictionary(a => a.Name, _ => new List<string>());
        var recommended = usable.Where(a => a.SecondsPerEmail <= AcceptableSeconds)
                              .OrderByDescending(a => a.Hungarian).ThenByDescending(a => a.Current).ThenBy(a => a.SecondsPerEmail).FirstOrDefault()
                          ?? usable.OrderBy(a => a.SecondsPerEmail).FirstOrDefault();
        if (recommended is not null)
        {
            tags[recommended.Name].Add("ajanlott");
            var faster = usable.Where(a => a.Name != recommended.Name && a.Hungarian >= 3 && a.SecondsPerEmail < recommended.SecondsPerEmail * 0.8)
                .OrderByDescending(a => a.Hungarian).ThenBy(a => a.SecondsPerEmail).FirstOrDefault();
            if (faster is not null)
                tags[faster.Name].Add("gyorsabb");
            var better = usable.Where(a => a.Name != recommended.Name && a.Hungarian > recommended.Hungarian && a.SecondsPerEmail <= 900)
                .OrderByDescending(a => a.Hungarian).ThenBy(a => a.SecondsPerEmail).FirstOrDefault();
            if (better is not null)
                tags[better.Name].Add("pontosabb");
        }

        return advice
            .Select(a => a with { Tags = tags[a.Name] })
            .OrderBy(a => a.Tags.Contains("ajanlott") ? 0 : a.Tags.Count > 0 ? 1 : a.Fit == "no" ? 3 : 2)
            .ThenByDescending(a => a.Hungarian).ThenBy(a => a.SecondsPerEmail ?? int.MaxValue)
            .ToList();
    }
}
