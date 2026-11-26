using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EfMailAgent.Llm;

/// <summary>Shared HTTP plumbing: per-request timeout, error classification, Hungarian error messages.</summary>
public abstract class HttpLlmClientBase(HttpClient httpClient, string baseUrl) : ILlmClient
{
    protected HttpClient Http { get; } = httpClient;
    protected string BaseUrl { get; } = baseUrl.TrimEnd('/');

    public abstract string Provider { get; }

    public abstract Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);

    public abstract Task<LlmHealth> CheckHealthAsync(CancellationToken cancellationToken);

    protected async Task<(JsonNode Body, TimeSpan Duration)> PostJsonAsync(string url, JsonObject payload, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, timeout, cancellationToken);
    }

    protected Task<(JsonNode Body, TimeSpan Duration)> GetJsonAsync(string url, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), timeout, cancellationToken);

    private async Task<(JsonNode Body, TimeSpan Duration)> SendAsync(Func<HttpRequestMessage> createRequest, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        using var request = createRequest();
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var kind = (int)response.StatusCode >= 500 ? LlmErrorKind.ServerError : LlmErrorKind.ClientError;
                throw new LlmTransportException(kind,
                    $"Az LLM szerver hibát adott: HTTP {(int)response.StatusCode} {response.ReasonPhrase} ({Shorten(ExtractError(text))})");
            }

            try
            {
                return (JsonNode.Parse(text) ?? throw new JsonException("üres válasz"), stopwatch.Elapsed);
            }
            catch (JsonException ex)
            {
                throw new LlmTransportException(LlmErrorKind.Other, $"Az LLM szerver válasza nem JSON: {Shorten(text)}", ex);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmTransportException(LlmErrorKind.Timeout,
                $"Az LLM nem válaszolt {timeout.TotalSeconds:0} másodpercen belül (időtúllépés).");
        }
        catch (HttpRequestException ex)
        {
            var unreachable = ex.InnerException is SocketException
                              || ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError;
            throw new LlmTransportException(unreachable ? LlmErrorKind.Unreachable : LlmErrorKind.Other,
                unreachable
                    ? $"Az LLM szerver nem érhető el ({BaseUrl}): {ex.Message}"
                    : $"Hiba az LLM szerverrel való kommunikációban ({BaseUrl}): {ex.Message}", ex);
        }
    }

    protected static string RequireString(JsonNode? node, string what)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        throw new LlmTransportException(LlmErrorKind.Other, $"Az LLM szerver válaszából hiányzik: {what}.");
    }

    protected static void RequireModel(LlmRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new LlmTransportException(LlmErrorKind.Configuration,
                "Nincs beállítva LLM modell (Llm.Model az appsettings.json-ban, vagy llm.model a szabályban).");
    }

    protected static LlmHealth Unhealthy(string provider, Exception ex, TimeSpan duration) =>
        new(false, provider, ex.Message, [], duration);

    internal static string Shorten(string? text, int max = 300)
    {
        text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string ExtractError(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            var error = node?["error"];
            if (error is JsonValue v && v.TryGetValue<string>(out var s))
                return s;
            if (error?["message"] is JsonValue m && m.TryGetValue<string>(out var message))
                return message;
        }
        catch (JsonException)
        {
        }
        return text;
    }
}
