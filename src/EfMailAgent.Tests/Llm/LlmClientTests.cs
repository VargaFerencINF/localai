using System.Text.Json.Nodes;
using EfMailAgent.Llm;
using EfMailAgent.Rules;
using EfMailAgent.Tests.Fakes;

namespace EfMailAgent.Tests.Llm;

public class LlmClientTests : IAsyncLifetime
{
    private FakeLlmServer _server = null!;

    public async Task InitializeAsync() => _server = await FakeLlmServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static LlmRequest Request(string model = "qwen3.5:9b") => new()
    {
        Messages = [new(LlmRoles.System, "rendszer"), new(LlmRoles.User, "felhasználó")],
        Schema = RuleSchemaBuilder.Build(RuleLoader.LoadAll(TestPaths.SampleRules).FindById("eloreendeles")!),
        SchemaName = "eloreendeles",
        Model = model,
        Temperature = 0.2,
        NumCtx = 8192,
        Timeout = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task Ollama_request_has_schema_format_and_options()
    {
        _server.Handler = (_, _) => new FakeReply("""{"rows": []}""");
        var client = new OllamaClient(new HttpClient(), _server.BaseUrl + "/");

        var response = await client.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal("""{"rows": []}""", response.Content);
        Assert.Equal(100, response.PromptTokens);
        var body = _server.ChatRequests.Single();
        Assert.Equal("qwen3.5:9b", body["model"]!.GetValue<string>());
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.False(body["think"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(Request().Schema, body["format"]));
        Assert.Equal(0.2, body["options"]!["temperature"]!.GetValue<double>());
        Assert.Equal(8192, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("felhasználó", body["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/v1")]
    public async Task OpenAi_request_uses_strict_json_schema(string suffix)
    {
        _server.Handler = (_, _) => new FakeReply("""{"rows": []}""");
        var client = new OpenAiCompatibleClient(new HttpClient(), _server.BaseUrl + suffix);

        var response = await client.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal("""{"rows": []}""", response.Content);
        var body = _server.ChatRequests.Single();
        var format = body["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.Equal("eloreendeles", format["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(format["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(Request().Schema, format["json_schema"]!["schema"]));
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task Health_lists_models_for_both_providers()
    {
        var ollama = await new OllamaClient(new HttpClient(), _server.BaseUrl).CheckHealthAsync(CancellationToken.None);
        var openAi = await new OpenAiCompatibleClient(new HttpClient(), _server.BaseUrl).CheckHealthAsync(CancellationToken.None);

        Assert.True(ollama.Reachable);
        Assert.Equal(["qwen3.5:9b", "llama3.2:3b"], ollama.Models);
        Assert.True(openAi.Reachable);
        Assert.Equal(["qwen3.5:9b", "llama3.2:3b"], openAi.Models);
    }

    [Fact]
    public async Task Health_of_unreachable_server_is_false_not_an_exception()
    {
        var health = await new OllamaClient(new HttpClient(), "http://127.0.0.1:1").CheckHealthAsync(CancellationToken.None);
        Assert.False(health.Reachable);
        Assert.Contains("nem érhető el", health.Error);
    }

    [Theory]
    [InlineData(500, LlmErrorKind.ServerError)]
    [InlineData(404, LlmErrorKind.ClientError)]
    public async Task Http_errors_are_classified(int status, LlmErrorKind kind)
    {
        _server.Handler = (_, _) => new FakeReply("", StatusCode: status);
        var client = new OllamaClient(new HttpClient(), _server.BaseUrl);

        var ex = await Assert.ThrowsAsync<LlmTransportException>(() => client.CompleteAsync(Request(), CancellationToken.None));

        Assert.Equal(kind, ex.Kind);
        Assert.Contains($"HTTP {status}", ex.Message);
        Assert.Contains("fake error", ex.Message);
    }

    [Fact]
    public async Task Timeout_and_unreachable_and_missing_model_are_classified()
    {
        _server.Handler = (_, _) => new FakeReply("{}", Delay: TimeSpan.FromSeconds(5));
        var slow = new OllamaClient(new HttpClient(), _server.BaseUrl);
        var timeout = await Assert.ThrowsAsync<LlmTransportException>(() =>
            slow.CompleteAsync(Request() with { Timeout = TimeSpan.FromMilliseconds(300) }, CancellationToken.None));
        Assert.Equal(LlmErrorKind.Timeout, timeout.Kind);
        Assert.False(timeout.IsOutage);

        var down = new OpenAiCompatibleClient(new HttpClient(), "http://127.0.0.1:1");
        var unreachable = await Assert.ThrowsAsync<LlmTransportException>(() => down.CompleteAsync(Request(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.Unreachable, unreachable.Kind);
        Assert.True(unreachable.IsOutage);

        var noModel = await Assert.ThrowsAsync<LlmTransportException>(() => slow.CompleteAsync(Request(model: ""), CancellationToken.None));
        Assert.Equal(LlmErrorKind.Configuration, noModel.Kind);
    }
}

public class MockLlmClientTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private MockLlmClient Client() => new(_tmp.Combine("mock"), 0, _tmp.Combine("dump"));

    private static LlmRequest Request(string subject = "Előrendelés", string? messageId = null, string? jobId = "job-1", int messages = 2) => new()
    {
        Messages = Enumerable.Range(0, messages).Select(i => new LlmMessage(i == 0 ? LlmRoles.System : LlmRoles.User, $"üzenet {i}")).ToArray(),
        Schema = new JsonObject(),
        SchemaName = "x",
        RuleId = "szabaly",
        Subject = subject,
        MessageId = messageId,
        JobId = jobId,
        Timeout = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Lookup_prefers_message_id_then_default_then_reports_missing()
    {
        var missing = await Assert.ThrowsAsync<LlmTransportException>(() => Client().CompleteAsync(Request(), CancellationToken.None));
        Assert.Contains("nincs mock válasz", missing.Message);
        Assert.False(missing.IsOutage);

        _tmp.WriteFile("mock/szabaly/default.json", "default");
        _tmp.WriteFile("mock/szabaly/by-message-id/abc_1@x.example.json", "egyedi");

        Assert.Equal("default", (await Client().CompleteAsync(Request(messageId: "<other@x>"), CancellationToken.None)).Content);
        Assert.Equal("egyedi", (await Client().CompleteAsync(Request(messageId: "<abc/1@x.example>"), CancellationToken.None)).Content);
    }

    [Fact]
    public async Task Scenario_tokens_override_the_lookup()
    {
        _tmp.WriteFile("mock/szabaly/default.json", "normal");
        _tmp.WriteFile("mock/_scenarios/invalid.json", "invalid");

        Assert.Equal("invalid", (await Client().CompleteAsync(Request("x [mock:invalid]"), CancellationToken.None)).Content);
        Assert.Equal("invalid", (await Client().CompleteAsync(Request("x [MOCK:invalid-then-valid]"), CancellationToken.None)).Content);
        Assert.Equal("normal", (await Client().CompleteAsync(Request("x [mock:invalid-then-valid]", messages: 4), CancellationToken.None)).Content);
        Assert.Equal("{\"rows\": []}", (await Client().CompleteAsync(Request("x [mock:empty]"), CancellationToken.None)).Content);
        var timeout = await Assert.ThrowsAsync<LlmTransportException>(() => Client().CompleteAsync(Request("x [mock:timeout]"), CancellationToken.None));
        Assert.Equal(LlmErrorKind.Timeout, timeout.Kind);
    }

    [Fact]
    public async Task Assembled_prompt_is_dumped_per_job()
    {
        _tmp.WriteFile("mock/szabaly/default.json", "normal");

        await Client().CompleteAsync(Request(), CancellationToken.None);
        await Client().CompleteAsync(Request(messages: 4), CancellationToken.None);
        await Client().CompleteAsync(Request(jobId: null), CancellationToken.None);

        var dump = File.ReadAllText(_tmp.Combine("dump", "job-1.txt"));
        Assert.Contains("Első hívás", dump);
        Assert.Contains("Javító hívás", dump);
        Assert.Contains("===== SYSTEM =====\nüzenet 0", dump);
        Assert.Single(Directory.GetFiles(_tmp.Combine("dump")));
    }

    [Fact]
    public async Task Health_depends_on_mock_folder()
    {
        Assert.False((await Client().CheckHealthAsync(CancellationToken.None)).Reachable);
        Directory.CreateDirectory(_tmp.Combine("mock"));
        Assert.True((await Client().CheckHealthAsync(CancellationToken.None)).Reachable);
    }
}
