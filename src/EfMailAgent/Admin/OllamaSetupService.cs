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

/// <summary>A model offered by the setup wizard. Sizes are approximate (download / memory needed).</summary>
public sealed record OllamaModelChoice(string Name, string Title, string Description, double DownloadGb, int MinRamGb);

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
    OllamaSetupOptions options, ILogger<OllamaSetupService> logger)
{
    // Checked against the Ollama library (ollama.com/library/qwen3.5, /gemma4) in November 2026; any other name can be typed in.
    public static readonly IReadOnlyList<OllamaModelChoice> Choices =
    [
        new("qwen3.5:4b", "Qwen3.5 4B", "Gyors, kisebb gépekre (processzoron is jól használható). Egyszerűbb, felsorolásos leveleknél elég lehet.", 3.3, 8),
        new("qwen3.5:9b", "Qwen3.5 9B", "Kiegyensúlyozott: jó magyar szövegértés és megbízható JSON. A legtöbb esetben ezt ajánljuk.", 6.6, 16),
        new("gemma4:e4b", "Gemma 4 E4B", "A Google modellje, a Qwen3.5 9B alternatívája; érdemes a Tesztkonzolban összevetni.", 6.6, 16),
        new("qwen3.5:27b", "Qwen3.5 27B", "A legpontosabb, de nagyon lassú videókártya nélkül; sok memóriát igényel.", 17, 32),
        new("qwen3.5:2b", "Qwen3.5 2B", "Nagyon kicsi és gyors, gyenge gépekre; összetettebb leveleknél pontatlanabb.", 2.7, 4),
    ];

    private readonly object _lock = new();
    private OllamaTaskState _task = new("", "idle", "", null, false, false, null);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*(/[a-z0-9._-]+)?(:[a-z0-9._-]+)?$")]
    private static partial Regex ModelNameRegex();

    public OllamaTaskState CurrentTask => _task;

    public string? CannotInstallReason =>
        !options.InstallSupported ? "Az Ollama automatikus telepítése csak Windowson érhető el."
        : runtime.IsWindowsService ? "Az EfMailAgent most Windows szolgáltatásként fut, ezért a telepítőt nem tudja a te asztalodon elindítani. Töltsd le és telepítsd az Ollamát a lenti linkről (bejelentkezett felhasználóként), utána folytasd itt a modellválasztással."
        : null;

    public string DefaultBaseUrl =>
        settings.Settings.Llm.Provider == LlmProviders.Ollama && !string.IsNullOrWhiteSpace(settings.Settings.Llm.BaseUrl)
            ? settings.Settings.Llm.BaseUrl.TrimEnd('/')
            : OllamaSetupOptions.DefaultBaseUrl;

    public static int RecommendedRamGb(long totalBytes) => (int)Math.Round(totalBytes / 1024d / 1024 / 1024);

    // The 27B model is never recommended automatically: on a processor it is too slow for daily use.
    public static string Recommend(int ramGb) => ramGb >= 14 ? "qwen3.5:9b" : ramGb >= 7 ? "qwen3.5:4b" : "qwen3.5:2b";

    public async Task<object> StatusAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        var url = NormalizeBaseUrl(baseUrl);
        string? version = null;
        string? error = null;
        IReadOnlyList<object> models = [];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var v = await http.GetFromJsonAsync<JsonObject>($"{url}/api/version", cancellationToken);
            version = v?["version"]?.GetValue<string>() ?? "?";
            var tags = await http.GetFromJsonAsync<JsonObject>($"{url}/api/tags", cancellationToken);
            models = (tags?["models"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (object)new
            {
                name = m["name"]?.GetValue<string>() ?? m["model"]?.GetValue<string>(),
                sizeGb = m["size"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? Math.Round(bytes / 1e9, 1) : (double?)null,
            }).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            error = ex is TaskCanceledException ? "időtúllépés" : ex.Message;
        }

        var ramGb = RecommendedRamGb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
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
            ramGb,
            recommended = Recommend(ramGb),
            choices = Choices,
            currentProvider = settings.Settings.Llm.Provider,
            currentModel = settings.Settings.Llm.Model,
            task = _task,
        };
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
