using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Tests.Fakes;

public sealed record FakeReply(string Content, int StatusCode = 200, TimeSpan Delay = default);

/// <summary>
/// In-process fake of Ollama (/api/chat, /api/tags) and OpenAI-compatible servers (/v1/chat/completions, /v1/models).
/// Replies come from <see cref="Handler"/>; every chat request body is recorded.
/// </summary>
public sealed class FakeLlmServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _inFlight;

    private FakeLlmServer(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = "";

    /// <summary>Called with (request body, zero-based call index).</summary>
    public Func<JsonObject, int, FakeReply> Handler { get; set; } = (_, _) => new FakeReply("""{"rows": []}""");

    public ConcurrentQueue<JsonObject> ChatRequests { get; } = new();

    public IReadOnlyList<string> Models { get; set; } = ["qwen3.5:9b", "llama3.2:3b"];

    public int MaxConcurrency { get; private set; }

    public int CallCount => ChatRequests.Count;

    /// <summary>NDJSON lines streamed by /api/pull (Ollama model download); the model is added to Models after "success".</summary>
    public Func<string, IReadOnlyList<string>> PullLines { get; set; } = model =>
    [
        """{"status":"pulling manifest"}""",
        """{"status":"pulling aaa","digest":"sha256:aaa","total":4000,"completed":1000}""",
        """{"status":"pulling aaa","digest":"sha256:aaa","total":4000,"completed":4000}""",
        """{"status":"verifying sha256 digest"}""",
        """{"status":"writing manifest"}""",
        """{"status":"success"}""",
    ];

    /// <summary>Served at /download/OllamaSetup.exe.</summary>
    public byte[] SetupBytes { get; set; } = new byte[300_000];

    public static async Task<FakeLlmServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new FakeLlmServer(app);

        app.MapPost("/api/chat", (Delegate)((HttpContext context) => server.ChatAsync(context, ollama: true)));
        app.MapPost("/v1/chat/completions", (Delegate)((HttpContext context) => server.ChatAsync(context, ollama: false)));
        app.MapGet("/api/tags", () => Results.Json(new { models = server.Models.Select(m => new { name = m, model = m }) }));
        app.MapGet("/api/version", () => Results.Json(new { version = "0.12.3" }));
        app.MapPost("/api/pull", async (HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            var model = body["model"]!.GetValue<string>();
            context.Response.ContentType = "application/x-ndjson";
            foreach (var line in server.PullLines(model))
            {
                await context.Response.WriteAsync(line + "\n");
                await context.Response.Body.FlushAsync();
                if (line.Contains("\"success\""))
                    server.Models = [.. server.Models, model];
            }
        });
        app.MapGet("/download/OllamaSetup.exe", () => Results.Bytes(server.SetupBytes, "application/octet-stream"));
        app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = server.Models.Select(m => new { id = m, @object = "model" }) }));

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    private async Task<IResult> ChatAsync(HttpContext context, bool ollama)
    {
        var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
        var index = ChatRequests.Count;
        ChatRequests.Enqueue(body);

        var current = Interlocked.Increment(ref _inFlight);
        lock (this)
            MaxConcurrency = Math.Max(MaxConcurrency, current);
        try
        {
            var reply = Handler(body, index);
            if (reply.Delay > TimeSpan.Zero)
                await Task.Delay(reply.Delay, context.RequestAborted);

            if (reply.StatusCode != 200)
                return Results.Json(new { error = $"fake error {reply.StatusCode}" }, statusCode: reply.StatusCode);

            return ollama
                ? Results.Json(new
                {
                    model = body["model"]?.GetValue<string>(),
                    message = new { role = "assistant", content = reply.Content },
                    done = true,
                    prompt_eval_count = 100,
                    eval_count = 50,
                })
                : Results.Json(new
                {
                    id = "chatcmpl-1",
                    model = body["model"]?.GetValue<string>(),
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = reply.Content }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 100, completion_tokens = 50 },
                });
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
