using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EfMailAgent.Llm;

/// <summary>Ollama native API: POST /api/chat (stream=false, format=JSON schema), health via GET /api/tags.</summary>
public sealed class OllamaClient(HttpClient httpClient, string baseUrl) : HttpLlmClientBase(httpClient, baseUrl)
{
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(5);

    public override string Provider => "ollama";

    public override async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        RequireModel(request);
        var options = new JsonObject { ["temperature"] = request.Temperature };
        if (request.NumCtx > 0)
            options["num_ctx"] = request.NumCtx;

        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages
                .Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["stream"] = false,
            ["format"] = request.Schema.DeepClone(),
            ["think"] = request.Think,
            ["options"] = options,
        };

        var (body, duration) = await PostJsonAsync($"{BaseUrl}/api/chat", payload, request.Timeout, cancellationToken);
        var content = RequireString(body["message"]?["content"], "message.content");
        var evalCount = (int?)body["eval_count"]?.GetValue<long>();
        var evalNs = body["eval_duration"]?.GetValue<long>();
        double? speed = evalCount is > 0 && evalNs is > 0 ? evalCount.Value / (evalNs.Value / 1e9) : null; // generation tokens per second
        return new LlmResponse(content, duration, body["model"]?.GetValue<string>(),
            (int?)body["prompt_eval_count"]?.GetValue<long>(), evalCount, speed);
    }

    public override async Task<LlmHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (body, duration) = await GetJsonAsync($"{BaseUrl}/api/tags", HealthTimeout, cancellationToken);
            var models = (body["models"] as JsonArray ?? [])
                .Select(m => m?["name"]?.GetValue<string>() ?? m?["model"]?.GetValue<string>())
                .OfType<string>()
                .ToArray();
            return new LlmHealth(true, Provider, null, models, duration);
        }
        catch (Exception ex) when (ex is LlmTransportException or InvalidOperationException)
        {
            return Unhealthy(Provider, ex, stopwatch.Elapsed);
        }
    }
}
