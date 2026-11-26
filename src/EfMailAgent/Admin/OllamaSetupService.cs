using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EfMailAgent.Configuration;
using EfMailAgent.Hosting;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Admin;

/// <summary>Progress of the one background task of the wizard (Ollama install or a model download).</summary>
public sealed record OllamaTaskState(string Kind, string Phase, string Message, double? Percent, bool Running, bool Failed, string? Model);

/// <summary>The Windows-only steps of installing Ollama; faked in tests.</summary>
public interface IOllamaPlatform
{
    /// <summary>Null if the file is a valid, Ollama-signed executable; otherwise the reason.</summary>
    Task<string?> VerifySignatureAsync(string file, CancellationToken cancellationToken);

    Task<int> RunInstallerAsync(string file, CancellationToken cancellationToken);

    /// <summary>Starts the Ollama tray app if it is installed but not running.</summary>
    void TryStartOllama();

    bool IsInstalled();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsOllamaPlatform : IOllamaPlatform
{
    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");

    public async Task<string?> VerifySignatureAsync(string file, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     $"$s = Get-AuthenticodeSignature -LiteralPath '{file.Replace("'", "''")}'; Write-Output ($s.Status.ToString() + '|' + $s.SignerCertificate.Subject)" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
        await process.WaitForExitAsync(cancellationToken);
        var parts = output.Split('|', 2);
        if (parts.Length != 2 || parts[0] != "Valid")
            return $"a digitális aláírás nem érvényes ({output})";
        return parts[1].Contains("Ollama", StringComparison.OrdinalIgnoreCase) ? null : $"nem az Ollama írta alá ({parts[1]})";
    }

    public async Task<int> RunInstallerAsync(string file, CancellationToken cancellationToken)
    {
        // Inno Setup installer, per user (no administrator rights needed); /SILENT shows only a progress window.
        using var process = Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-") { UseShellExecute = false })!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode;
    }

    public void TryStartOllama()
    {
        var app = Path.Combine(InstallDir, "ollama app.exe");
        if (File.Exists(app))
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true })?.Dispose();
    }

    public bool IsInstalled() => File.Exists(Path.Combine(InstallDir, "ollama.exe")) || File.Exists(Path.Combine(InstallDir, "ollama app.exe"));
}

/// <summary>Non-Windows (development): installing is not offered.</summary>
public sealed class UnsupportedOllamaPlatform : IOllamaPlatform
{
    public Task<string?> VerifySignatureAsync(string file, CancellationToken cancellationToken) => Task.FromResult<string?>("nem Windows");
    public Task<int> RunInstallerAsync(string file, CancellationToken cancellationToken) => Task.FromResult(-1);
    public void TryStartOllama() { }
    public bool IsInstalled() => false;
}

public sealed class OllamaSetupOptions
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434";
    public string DownloadUrl { get; init; } = "https://ollama.com/download/OllamaSetup.exe";
    public TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public bool InstallSupported { get; init; } = OperatingSystem.IsWindows();
}

/// <summary>
/// The guided Ollama setup on the Beállítások page: detect Ollama, download and install it (Windows, when started by a
/// person), recommend and download a model with progress, then switch the settings to it. One background task at a time.
/// </summary>
public sealed partial class OllamaSetupService(
    EffectiveSettings settings, SettingsService settingsService, RuntimeInfo runtime, IOllamaPlatform platform,
    OllamaSetupOptions options, ILogger<OllamaSetupService> logger, ISystemMetrics metrics, Jobs.JobQueue queue)
{
    public const string BenchmarkFileName = "model-benchmarks.json";

    private readonly object _lock = new();
    private OllamaTaskState _task = new("", "idle", "", null, false, false, null);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*(/[a-z0-9._-]+)?(:[a-z0-9._-]+)?$")]
    private static partial Regex ModelNameRegex();

    public OllamaTaskState CurrentTask => _task;

    public ModelCatalog Catalog { get; init; } = ModelCatalog.BuiltIn;

    public string? CannotInstallReason =>
        !options.InstallSupported ? "Az Ollama automatikus telepítése csak Windowson érhető el."
        : runtime.IsWindowsService ? "Az EfMailAgent most Windows szolgáltatásként fut, ezért a telepítőt nem tudja a te asztalodon elindítani. Töltsd le és telepítsd az Ollamát a lenti linkről (bejelentkezett felhasználóként), utána folytasd itt a modellválasztással."
        : null;

    public string DefaultBaseUrl =>
        settings.Settings.Llm.Provider == LlmProviders.Ollama && !string.IsNullOrWhiteSpace(settings.Settings.Llm.BaseUrl)
            ? settings.Settings.Llm.BaseUrl.TrimEnd('/')
            : OllamaSetupOptions.DefaultBaseUrl;

    private string CurrentOllamaModel => settings.Settings.Llm.Provider == LlmProviders.Ollama ? settings.Settings.Llm.Model : "";

    public async Task<object> StatusAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        var url = NormalizeBaseUrl(baseUrl);
        string? version = null;
        string? error = null;
        List<JsonObject> installed = [];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var v = await http.GetFromJsonAsync<JsonObject>($"{url}/api/version", cancellationToken);
            version = v?["version"]?.GetValue<string>() ?? "?";
            var tags = await http.GetFromJsonAsync<JsonObject>($"{url}/api/tags", cancellationToken);
            installed = (tags?["models"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            error = ex is TaskCanceledException ? "időtúllépés" : ex.Message;
        }

        var current = CurrentOllamaModel;
        var models = installed.Select(m =>
        {
            var name = m["name"]?.GetValue<string>() ?? m["model"]?.GetValue<string>() ?? "";
            var size = m["size"] is JsonValue sv && sv.TryGetValue<long>(out var bytes) ? Math.Round(bytes / 1e9, 1) : (double?)null;
            var successor = Catalog.SuccessorOf(name);
            return new
            {
                name,
                sizeGb = size,
                modifiedAt = m["modified_at"]?.GetValue<string>(),
                inUse = SameModel(name, current),
                successor,
                successorInstalled = successor is not null && installed.Any(o => SameModel(o["name"]?.GetValue<string>() ?? "", successor)),
                catalog = Catalog.Find(name) is not null,
            };
        }).OrderBy(m => m.successor is null ? 1 : 0).ThenBy(m => m.name).ToList();

        var hardware = metrics.Hardware();
        var measured = MeasuredSpeeds();
        var advice = ModelAdvisor.Advise(Catalog, hardware, measured);
        return new
        {
            baseUrl = url,
            reachable = version is not null,
            version,
            error,
            installed = version is not null || platform.IsInstalled(),
            models,
            canInstall = CannotInstallReason is null,
            cannotInstallReason = CannotInstallReason,
            downloadPage = "https://ollama.com/download/windows",
            hardware,
            ramGb = (int)Math.Round(hardware.TotalRamGb),
            catalogUpdated = Catalog.Updated,
            advice,
            recommended = advice.FirstOrDefault(a => a.Tags.Contains("ajanlott"))?.Name,
            currentProvider = settings.Settings.Llm.Provider,
            currentModel = settings.Settings.Llm.Model,
            task = _task,
        };
    }

    public static bool SameModel(string a, string b)
    {
        static string N(string x) => x.Contains(':') ? x.Trim() : x.Trim() + ":latest";
        return a.Length > 0 && b.Length > 0 && N(a).Equals(N(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Speeds measured on this machine: quick tests (saved) and the median of real jobs per model.</summary>
    public IReadOnlyDictionary<string, ModelSpeed> MeasuredSpeeds()
    {
        var result = new Dictionary<string, ModelSpeed>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in queue.RecentLlmUsage(200).Where(r => r.Model is not null && r.TokensPerSecond is > 0).GroupBy(r => r.Model!))
        {
            var speeds = group.Select(r => r.TokensPerSecond!.Value).Order().ToList();
            result[group.Key] = new ModelSpeed(Math.Round(speeds[speeds.Count / 2], 1), null, $"{speeds.Count} feldolgozott levél alapján");
        }
        foreach (var (model, speed) in LoadBenchmarks())
            result[model] = speed; // a quick test also knows the prompt speed
        return result;
    }

    private string BenchmarkPath => Path.Combine(settings.DataDir, BenchmarkFileName);

    private Dictionary<string, ModelSpeed> LoadBenchmarks()
    {
        try
        {
            return File.Exists(BenchmarkPath)
                ? JsonSerializer.Deserialize<Dictionary<string, ModelSpeed>>(File.ReadAllText(BenchmarkPath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// "Gyors próba": a short Hungarian request to the model; measures prompt and generation speed (from Ollama's own
    /// timings), saves them, and estimates the time of one email.
    /// </summary>
    public async Task<object> BenchmarkAsync(string? model, string? baseUrl, CancellationToken cancellationToken)
    {
        model = model?.Trim() ?? "";
        if (!ModelNameRegex().IsMatch(model))
            throw new AdminRequestException("Érvénytelen modellnév.", 400);
        var url = NormalizeBaseUrl(baseUrl);
        var text = string.Concat(Enumerable.Repeat(
            "Kedves Péter! A tavaszi szezonra az alábbiakat szeretnénk előrendelni: feeder bot 3,6 m 10 db, monofil zsinór 0,25 mm 40 tekercs, "
            + "horog 12-es 25 csomag, etetőanyag 3 karton. Szállítás március elején. ", 8));
        var payload = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["think"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Sorold fel röviden a rendelt tételeket mennyiséggel:\n\n" + text }),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_predict"] = 120 },
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        JsonObject body;
        try
        {
            using var response = await http.PostAsJsonAsync($"{url}/api/chat", payload, cancellationToken);
            body = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
            if (!response.IsSuccessStatusCode)
                throw new AdminRequestException($"Az Ollama hibát jelzett: {body["error"]?.GetValue<string>() ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}", 502);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new AdminRequestException($"Az Ollama nem érhető el ({url}): {ex.Message}", 502);
        }

        double? Rate(string count, string duration) =>
            body[count]?.GetValue<long>() is long c && c > 0 && body[duration]?.GetValue<long>() is long d && d > 0 ? Math.Round(c / (d / 1e9), 1) : null;
        var gen = Rate("eval_count", "eval_duration") ?? throw new AdminRequestException("Az Ollama nem adott vissza időmérést.", 502);
        var prompt = Rate("prompt_eval_count", "prompt_eval_duration");
        var speed = new ModelSpeed(gen, prompt, "Gyors próba " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        var all = LoadBenchmarks();
        all[model] = speed;
        Directory.CreateDirectory(settings.DataDir);
        File.WriteAllText(BenchmarkPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        var seconds = (int)Math.Round(ModelAdvisor.PromptTokens / (prompt ?? gen * 12) + ModelAdvisor.OutputTokens / gen);
        logger.LogInformation("Gyors próba: {Model}, {Gen} token/mp (prompt: {Prompt} token/mp), becsült {Seconds} mp/levél", model, gen, prompt, seconds);
        return new { model, generationTokPerSec = gen, promptTokPerSec = prompt, secondsPerEmail = seconds, loadSeconds = Rate2(body["load_duration"]) };

        static double? Rate2(JsonNode? ns) => ns?.GetValue<long>() is long v ? Math.Round(v / 1e9, 1) : null;
    }

    public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Errors, double FreedGb, string? SwitchedTo);

    /// <summary>
    /// Deletes downloaded models (independently of updating them). The model the program uses is only deleted together
    /// with <paramref name="switchTo"/>, another downloaded model the settings are switched to first.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(IReadOnlyList<string>? models, string? switchTo, string? baseUrl, CancellationToken cancellationToken)
    {
        var names = (models ?? []).Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            throw new AdminRequestException("Válassz ki legalább egy modellt a törléshez.", 400);
        if (names.FirstOrDefault(n => !ModelNameRegex().IsMatch(n)) is { } bad)
            throw new AdminRequestException($"Érvénytelen modellnév: {bad}", 400);
        var url = NormalizeBaseUrl(baseUrl);

        string? switched = null;
        var current = CurrentOllamaModel;
        if (names.Any(n => SameModel(n, current)))
        {
            if (string.IsNullOrWhiteSpace(switchTo))
                throw new AdminRequestException($"A program most ezt a modellt használja: {current}. Előbb válassz másik modellt, amire átálljon, vagy ne töröld.", 409);
            if (names.Any(n => SameModel(n, switchTo)))
                throw new AdminRequestException("Arra a modellre nem lehet átállni, amit éppen törölsz.", 400);
            var applied = Apply(switchTo, url);
            if (!applied.Saved)
                throw new AdminRequestException(string.Join(" ", applied.Errors), 422);
            switched = switchTo.Trim();
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var tags = await http.GetFromJsonAsync<JsonObject>($"{url}/api/tags", cancellationToken);
            foreach (var m in (tags?["models"] as JsonArray ?? []).OfType<JsonObject>())
                if (m["name"]?.GetValue<string>() is { } n && m["size"] is JsonValue sv && sv.TryGetValue<long>(out var b))
                    sizes[n] = b / 1e9;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new AdminRequestException($"Az Ollama nem érhető el ({url}): {ex.Message}", 502);
        }

        var deleted = new List<string>();
        var errors = new List<string>();
        double freed = 0;
        foreach (var name in names)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{url}/api/delete") { Content = JsonContent.Create(new { model = name, name }) };
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    deleted.Add(name);
                    freed += sizes.GetValueOrDefault(name);
                }
                else
                {
                    errors.Add($"{name}: {TryError(await response.Content.ReadAsStringAsync(cancellationToken)) ?? $"HTTP {(int)response.StatusCode}"}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        logger.LogInformation("Modellek törölve: {Deleted}{Errors}", string.Join(", ", deleted), errors.Count > 0 ? "; hibák: " + string.Join("; ", errors) : "");
        return new DeleteResult(deleted, errors, Math.Round(freed, 1), switched);
    }

    public void StartInstall()
    {
        if (CannotInstallReason is { } reason)
            throw new AdminRequestException(reason, 409);
        Begin(new OllamaTaskState("install", "letoltes", "Az Ollama telepítő letöltése…", 0, true, false, null));
        _ = System.Threading.Tasks.Task.Run(InstallAsync);
    }

    public void StartPull(string? model, string? baseUrl)
    {
        model = model?.Trim() ?? "";
        if (!ModelNameRegex().IsMatch(model) || model.Length > 120)
            throw new AdminRequestException("Érvénytelen modellnév. Példa: qwen3.5:9b", 400);
        var url = NormalizeBaseUrl(baseUrl);
        Begin(new OllamaTaskState("pull", "letoltes", $"A(z) {model} modell letöltésének indítása…", 0, true, false, model));
        _ = System.Threading.Tasks.Task.Run(() => PullAsync(model, url));
    }

    /// <summary>Switches the AI settings to Ollama with this model (applied at once, like the Beállítások form).</summary>
    public SettingsSaveResult Apply(string? model, string? baseUrl)
    {
        model = model?.Trim() ?? "";
        if (!ModelNameRegex().IsMatch(model))
            throw new AdminRequestException("Válassz egy letöltött modellt.", 400);
        var current = settingsService.Get();
        var llm = current.Llm with { Provider = LlmProviders.Ollama, BaseUrl = NormalizeBaseUrl(baseUrl), Model = model };
        var result = settingsService.Save(new SettingsUpdate(llm, current.Csv, current.OutputFolder, current.Port));
        if (result.Saved)
            logger.LogInformation("AI beállítva a varázslóval: Ollama, {Model}, {BaseUrl}", model, llm.BaseUrl);
        return result;
    }

    private void Begin(OllamaTaskState state)
    {
        lock (_lock)
        {
            if (_task.Running)
                throw new AdminRequestException("Már fut egy telepítés vagy letöltés; várd meg, amíg befejeződik.", 409);
            _task = state;
        }
    }

    private void Update(OllamaTaskState state)
    {
        lock (_lock)
            _task = state;
    }

    private string NormalizeBaseUrl(string? baseUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new AdminRequestException("Az Ollama címe érvénytelen (példa: http://127.0.0.1:11434).", 400);
        return url;
    }

    private async Task InstallAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), "EfMailAgent", "OllamaSetup.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            using (var response = await http.GetAsync(options.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(file);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    Update(_task with
                    {
                        Percent = total is > 0 ? Math.Round(done * 100d / total.Value, 1) : null,
                        Message = $"Az Ollama telepítő letöltése… {done / 1024d / 1024:0} MB" + (total is > 0 ? $" / {total.Value / 1024d / 1024:0} MB" : ""),
                    });
                }
            }

            Update(_task with { Phase = "ellenorzes", Message = "A telepítő digitális aláírásának ellenőrzése…", Percent = null });
            if (await platform.VerifySignatureAsync(file, CancellationToken.None) is { } signatureError)
            {
                File.Delete(file);
                Fail("install", $"A letöltött telepítőt nem indítottam el: {signatureError}.");
                return;
            }

            Update(_task with { Phase = "telepites", Message = "Az Ollama telepítése… (a telepítő saját ablakában látszik a folyamat)" });
            var code = await platform.RunInstallerAsync(file, CancellationToken.None);
            if (code != 0)
            {
                Fail("install", $"Az Ollama telepítője hibával állt le (kód: {code}).");
                return;
            }

            Update(_task with { Phase = "inditas", Message = "Az Ollama indulására várok…" });
            var started = DateTimeOffset.UtcNow;
            var triedStart = false;
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            while (DateTimeOffset.UtcNow - started < options.StartWait)
            {
                try
                {
                    using var response = await probe.GetAsync($"{OllamaSetupOptions.DefaultBaseUrl}/api/version");
                    if (response.IsSuccessStatusCode)
                    {
                        Update(new OllamaTaskState("install", "kesz", "Az Ollama telepítve és fut. Válassz modellt!", 100, false, false, null));
                        logger.LogInformation("Ollama telepítve a varázslóval.");
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                }
                if (!triedStart && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(10))
                {
                    platform.TryStartOllama();
                    triedStart = true;
                }
                await System.Threading.Tasks.Task.Delay(options.PollInterval);
            }
            Fail("install", "Az Ollama települt, de nem indult el. Indítsd el a Start menüből (Ollama), majd kattints az Újraellenőrzésre.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            Fail("install", $"Az Ollama telepítése nem sikerült: {ex.Message}");
        }
    }

    private async Task PullAsync(string model, string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/pull")
            {
                Content = JsonContent.Create(new { model, stream = true }),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            {
                var body = await response.Content.ReadAsStringAsync();
                Fail("pull", PullError(model, TryError(body) ?? $"HTTP {(int)response.StatusCode}"), model);
                return;
            }

            var layers = new Dictionary<string, (long Total, long Completed)>();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                    continue;
                var json = JsonNode.Parse(line) as JsonObject;
                if (json is null)
                    continue;
                if (json["error"]?.GetValue<string>() is { } error)
                {
                    Fail("pull", PullError(model, error), model);
                    return;
                }
                var status = json["status"]?.GetValue<string>() ?? "";
                if (json["digest"]?.GetValue<string>() is { } digest && json["total"] is JsonValue t && t.TryGetValue<long>(out var total))
                {
                    var completed = json["completed"] is JsonValue c && c.TryGetValue<long>(out var value) ? value : 0;
                    layers[digest] = (total, completed);
                }
                var sumTotal = layers.Values.Sum(l => l.Total);
                var sumDone = layers.Values.Sum(l => l.Completed);
                var percent = sumTotal > 0 ? Math.Round(sumDone * 100d / sumTotal, 1) : (double?)null;
                if (status == "success")
                {
                    Update(new OllamaTaskState("pull", "kesz", $"A(z) {model} modell letöltve.", 100, false, false, model));
                    logger.LogInformation("Ollama modell letöltve a varázslóval: {Model}", model);
                    return;
                }
                Update(new OllamaTaskState("pull", "letoltes", PullMessage(model, status, sumDone, sumTotal), percent, true, false, model));
            }
            Fail("pull", $"A(z) {model} letöltése megszakadt. Próbáld újra.", model);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            Fail("pull", $"Az Ollama nem érhető el ({baseUrl}): {ex.Message}", model);
        }
    }

    private static string PullMessage(string model, string status, long done, long total)
    {
        if (status.StartsWith("pulling manifest", StringComparison.Ordinal))
            return $"A(z) {model} modell adatainak lekérése…";
        if (status.StartsWith("verifying", StringComparison.Ordinal))
            return "A letöltött fájlok ellenőrzése…";
        if (status.StartsWith("writing", StringComparison.Ordinal))
            return "Mentés…";
        return total > 0
            ? string.Create(CultureInfo.GetCultureInfo("hu-HU"), $"A(z) {model} letöltése… {done / 1e9:0.0} GB / {total / 1e9:0.0} GB")
            : $"A(z) {model} letöltése…";
    }

    private static string PullError(string model, string error) =>
        error.Contains("file does not exist", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? $"Nincs ilyen modell az Ollama könyvtárában: {model}. Ellenőrizd a nevet az ollama.com/library oldalon."
            : $"Az Ollama hibát jelzett a(z) {model} letöltésekor: {error}";

    private static string? TryError(string body)
    {
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Fail(string kind, string message, string? model = null)
    {
        logger.LogWarning("Ollama varázsló: {Message}", message);
        Update(new OllamaTaskState(kind, "hiba", message, null, false, true, model));
    }
}
