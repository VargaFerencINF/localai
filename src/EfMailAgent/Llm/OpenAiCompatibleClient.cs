using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EfMailAgent.Llm;

/// <summary>
/// OpenAI-compatible local servers (LM Studio, llama.cpp server, vLLM): POST /v1/chat/completions with
/// response_format json_schema (strict), health via GET /v1/models. A base URL ending in /v1 is accepted too.
/// </summary>
public sealed class OpenAiCompatibleClient(HttpClient httpClient, string baseUrl)
    : HttpLlmClientBase(httpClient, StripV1(baseUrl))
{
    public override string Provider => "openai_compatible";

    public override async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        RequireModel(request);
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages
                .Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["temperature"] = request.Temperature,
            ["stream"] = false,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["schema"] = request.Schema.DeepClone(),
                    ["strict"] = true,
                },
            },
        };

        var (body, duration) = await PostJsonAsync($"{BaseUrl}/v1/chat/completions", payload, request.Timeout, cancellationToken);
        var content = RequireString(body["choices"]?[0]?["message"]?["content"], "choices[0].message.content");
        var usage = body["usage"];
        var completion = (int?)usage?["completion_tokens"]?.GetValue<long>();
        double? speed = completion is > 0 && duration.TotalSeconds > 0 ? completion.Value / duration.TotalSeconds : null; // includes prompt time
        return new LlmResponse(content, duration, body["model"]?.GetValue<string>(),
            (int?)usage?["prompt_tokens"]?.GetValue<long>(), completion, speed);
    }

    public override async Task<LlmHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (body, duration) = await GetJsonAsync($"{BaseUrl}/v1/models", OllamaClient.HealthTimeout, cancellationToken);
            var models = (body["data"] as JsonArray ?? [])
                .Select(m => m?["id"]?.GetValue<string>())
                .OfType<string>()
                .ToArray();
            return new LlmHealth(true, Provider, null, models, duration);
        }
        catch (Exception ex) when (ex is LlmTransportException or InvalidOperationException)
        {
            return Unhealthy(Provider, ex, stopwatch.Elapsed);
        }
    }

    private static string StripV1(string url)
    {
        url = url.TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
    }
}
