using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Api;
using EfMailAgent.Jobs;
using EfMailAgent.Tests.Processing;

namespace EfMailAgent.Tests.Api;

public class ApiTests : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.StartAsync();

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Theory]
    [InlineData(null)]
    [InlineData("rossz-token")]
    [InlineData("")]
    public async Task Missing_or_wrong_token_gives_401_without_details(string? token)
    {
        var client = _api.App.GetTestClientWithoutToken();
        if (token is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation(ApiGuardMiddleware.TokenHeader, token);

        foreach (var path in new[] { "/api/health", "/api/matchers", "/api/jobs", "/api/openapi.json" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/jobs", ApiFixture.Payload())).StatusCode);
    }

    [Fact]
    public async Task Health_reports_status_version_llm_queue_and_rules()
    {
        var health = await _api.Client.GetFromJsonAsync<JsonObject>("/api/health");

        Assert.Equal("ok", health!["status"]!.GetValue<string>());
        Assert.Equal(VersionInfo.Current, health["version"]!.GetValue<string>());
        Assert.True(health["llmReachable"]!.GetValue<bool>());
        Assert.Equal(0, health["queueLength"]!.GetValue<int>());
        Assert.Equal(16, health["rulesVersion"]!.GetValue<string>().Length);
        Assert.Equal("mock", health["llmProvider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Matchers_have_contract_shape_and_support_etag()
    {
        var response = await _api.Client.GetAsync("/api/matchers");
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var etag = response.Headers.ETag!;

        Assert.Equal($"\"{body["rulesVersion"]!.GetValue<string>()}\"", etag.Tag);
        var matcher = body["matchers"]!.AsArray().Single()!;
        Assert.Equal("eloreendeles", matcher["ruleId"]!.GetValue<string>());
        Assert.Equal("Előrendelés feldolgozás", matcher["name"]!.GetValue<string>());
        Assert.Equal(["előrendelés", "elorendeles", "pre-order"], matcher["subjectContains"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(matcher["subjectRegex"]);
        Assert.Equal(10, matcher["priority"]!.GetValue<int>());

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/matchers");
        conditional.Headers.IfNoneMatch.Add(etag);
        var notModified = await _api.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal("", await notModified.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Matchers_change_version_and_hide_disabled_rules_after_reload()
    {
        var first = (await _api.Client.GetFromJsonAsync<JsonObject>("/api/matchers"))!["rulesVersion"]!.GetValue<string>();
        var yaml = Path.Combine(_api.Settings.RulesDir, "eloreendeles", "rule.yaml");
        File.WriteAllText(yaml, File.ReadAllText(yaml).Replace("enabled: true", "enabled: false"));
        _api.App.Services.GetRequiredServiceRules().Reload();

        var body = (await _api.Client.GetFromJsonAsync<JsonObject>("/api/matchers"))!;
        Assert.NotEqual(first, body["rulesVersion"]!.GetValue<string>());
        Assert.Empty(body["matchers"]!.AsArray());
    }

    [Fact]
    public async Task Submit_returns_202_then_200_for_duplicate()
    {
        var first = await _api.SubmitAsync(ApiFixture.Payload());
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("eloreendeles", created["ruleId"]!.GetValue<string>());
        Assert.Equal("queued", created["status"]!.GetValue<string>());
        Assert.False(created["duplicate"]!.GetValue<bool>());

        // Thunderbird's headerMessageId has no angle brackets; it must still be recognized.
        var second = await _api.SubmitAsync(ApiFixture.Payload(messageId: "api-1@x.example"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var duplicate = (await second.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(created["jobId"]!.GetValue<string>(), duplicate["jobId"]!.GetValue<string>());
        Assert.True(duplicate["duplicate"]!.GetValue<bool>());

        var job = _api.Queue.Get(created["jobId"]!.GetValue<string>())!;
        Assert.Equal("<api-1@x.example>", job.MessageId);
        Assert.Equal("eloreendeles@x.example, masik@x.example", job.Recipients);
        Assert.Equal(["rendeles.xlsx"], job.AttachmentNames);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 41, 0, TimeSpan.Zero), job.ReceivedAt);
        Assert.Equal(JobSource.Auto, job.Source);
    }

    [Fact]
    public async Task Submit_without_matching_rule_gives_422()
    {
        var response = await _api.SubmitAsync(ApiFixture.Payload(subject: "Fwd: Szállítási cím változás"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("egyik bekapcsolt szabályra sem illeszkedik", await response.Content.ReadAsStringAsync());
        Assert.Empty(_api.Queue.List());
    }

    [Fact]
    public async Task Explicit_rule_id_skips_subject_matching_and_must_exist()
    {
        var manual = await _api.SubmitAsync(ApiFixture.Payload(subject: "Valami más", ruleId: "eloreendeles", source: "manual"));
        Assert.Equal(HttpStatusCode.Accepted, manual.StatusCode);
        Assert.Equal(JobSource.Manual, _api.Queue.List().Single().Source);

        var unknown = await _api.SubmitAsync(ApiFixture.Payload(messageId: "<other@x>", ruleId: "nincs"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("Ismeretlen vagy kikapcsolt szabály", await unknown.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("", "Kérünk", "messageId")]
    [InlineData("<x@y>", null, "bodyText")]
    public async Task Missing_required_fields_give_400_with_field_errors(string messageId, string? body, string field)
    {
        var response = await _api.SubmitAsync(ApiFixture.Payload(messageId: messageId, bodyText: body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(error["errors"]!.AsObject().ContainsKey(field));
    }

    [Fact]
    public async Task Invalid_source_date_and_json_give_400()
    {
        var badSource = await _api.SubmitAsync(ApiFixture.Payload(source: "robot"));
        Assert.Equal(HttpStatusCode.BadRequest, badSource.StatusCode);

        var badDate = await _api.Client.PostAsync("/api/jobs", Json("""{"messageId":"<d@x>","subject":"előrendelés","bodyText":"x","receivedAt":"tegnap"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
        Assert.Contains("receivedAt", await badDate.Content.ReadAsStringAsync());

        var badJson = await _api.Client.PostAsync("/api/jobs", Json("{ nem json"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Contains("nem érvényes JSON", await badJson.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Html_only_body_is_converted_to_text()
    {
        var response = await _api.SubmitAsync(ApiFixture.Payload(bodyText: null,
            bodyHtml: "<html><style>x{}</style><body><p>Kérünk:</p><ul><li>10 db bot</li></ul></body></html>"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = _api.Queue.List().Single();
        Assert.Equal("Kérünk:\n\n- 10 db bot", job.BodyText);
        Assert.Contains("<li>", job.BodyHtml);
    }

    [Fact]
    public async Task Oversized_body_gives_413()
    {
        var huge = ApiFixture.Payload(bodyText: new string('x', 2 * 1024 * 1024 + 10));
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(huge));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await _api.Client.PostAsync("/api/jobs", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_api.Queue.List());
    }

    [Fact]
    public async Task Get_and_list_jobs()
    {
        var created = (await (await _api.SubmitAsync(ApiFixture.Payload())).Content.ReadFromJsonAsync<JsonObject>())!;
        var id = created["jobId"]!.GetValue<string>();

        var job = (await _api.Client.GetFromJsonAsync<JsonObject>($"/api/jobs/{id}"))!;
        foreach (var field in new[] { "id", "status", "ruleId", "attempts", "error", "outputFile", "rowCount", "createdAt", "finishedAt" })
            Assert.True(job.ContainsKey(field), field);
        Assert.Equal(id, job["id"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NotFound, (await _api.Client.GetAsync("/api/jobs/nincs-ilyen")).StatusCode);

        await JobWorkerTests.WaitUntil(() => JobStatus.IsTerminal(_api.Queue.Get(id)!.Status), TimeSpan.FromSeconds(10));
        var list = (await _api.Client.GetFromJsonAsync<JsonObject>("/api/jobs?status=needs_review&limit=10"))!;
        Assert.Equal(id, list["jobs"]!.AsArray().Single()!["id"]!.GetValue<string>());
        var none = (await _api.Client.GetFromJsonAsync<JsonObject>("/api/jobs?status=failed"))!;
        Assert.Empty(none["jobs"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.Client.GetAsync("/api/jobs?status=ismeretlen")).StatusCode);
    }

    [Fact]
    public async Task Submitted_demo_email_is_processed_by_the_worker_into_the_expected_csv()
    {
        var demo = EfMailAgent.Email.EmailFile.Load(Path.Combine(TestPaths.SamplesRoot, "emails", "demo_eloreendeles_01.eml"));
        var response = await _api.SubmitAsync(new
        {
            messageId = "fwd-demo-01@ertekesites.example",
            receivedAt = demo.ReceivedAt!.Value.ToString("O"),
            from = demo.From,
            to = demo.To,
            subject = demo.Subject,
            bodyText = demo.BodyText,
            source = "auto",
        });
        var id = (await response.Content.ReadFromJsonAsync<JsonObject>())!["jobId"]!.GetValue<string>();

        await JobWorkerTests.WaitUntil(() => JobStatus.IsTerminal(_api.Queue.Get(id)!.Status), TimeSpan.FromSeconds(10));

        var job = (await _api.Client.GetFromJsonAsync<JsonObject>($"/api/jobs/{id}"))!;
        Assert.Equal("needs_review", job["status"]!.GetValue<string>());
        Assert.Equal(5, job["rowCount"]!.GetValue<int>());
        var csv = job["outputFile"]!.GetValue<string>();
        Assert.Equal(
            MockPipelineTests.MaskVolatileColumns(File.ReadAllBytes(Path.Combine(TestPaths.SamplesRoot, "expected", "demo_eloreendeles_01.csv"))),
            MockPipelineTests.MaskVolatileColumns(File.ReadAllBytes(csv)));
    }

    [Fact]
    public async Task Retry_requeues_failed_and_needs_review_only()
    {
        var created = (await (await _api.SubmitAsync(ApiFixture.Payload(subject: "Előrendelés [mock:invalid]"))).Content.ReadFromJsonAsync<JsonObject>())!;
        var id = created["jobId"]!.GetValue<string>();
        await JobWorkerTests.WaitUntil(() => _api.Queue.Get(id)!.Status == JobStatus.Failed, TimeSpan.FromSeconds(10));

        var retry = await _api.Client.PostAsync($"/api/jobs/{id}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        await JobWorkerTests.WaitUntil(() => _api.Queue.Get(id)!.Status == JobStatus.Failed && _api.Queue.Get(id)!.Attempts == 1, TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Client.PostAsync("/api/jobs/nincs/retry", null)).StatusCode);

        var queued = (await (await _api.SubmitAsync(ApiFixture.Payload(messageId: "<q@x>", subject: "Előrendelés [mock:timeout]"))).Content.ReadFromJsonAsync<JsonObject>())!;
        var conflict = await _api.Client.PostAsync($"/api/jobs/{queued["jobId"]!.GetValue<string>()}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Cors_allows_only_thunderbird_extension_origins()
    {
        var allowed = await Preflight("moz-extension://4f9c2d1e-0000-4000-8000-123456789abc");
        Assert.Equal("moz-extension://4f9c2d1e-0000-4000-8000-123456789abc", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("X-EfMailAgent-Token", allowed.Headers.GetValues("Access-Control-Allow-Headers").Single());

        foreach (var origin in new[] { "http://evil.example", "chrome-extension://abc", "null" })
            Assert.False((await Preflight(origin)).Headers.Contains("Access-Control-Allow-Origin"), origin);

        var actual = new HttpRequestMessage(HttpMethod.Get, "/api/matchers");
        actual.Headers.Add("Origin", "moz-extension://abc");
        var response = await _api.Client.SendAsync(actual);
        Assert.Equal("moz-extension://abc", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("ETag", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task OpenApi_document_describes_every_endpoint()
    {
        var document = (await _api.Client.GetFromJsonAsync<JsonObject>(ApiHost.OpenApiPath))!;

        var paths = document["paths"]!.AsObject();
        foreach (var path in new[] { "/api/health", "/api/matchers", "/api/jobs", "/api/jobs/{id}", "/api/jobs/{id}/retry" })
            Assert.True(paths.ContainsKey(path), path);
        Assert.NotNull(document["components"]!["schemas"]!["SubmitJobRequest"]);
        Assert.Equal(ApiGuardMiddleware.TokenHeader, document["components"]!["securitySchemes"]!["token"]!["name"]!.GetValue<string>());
    }

    private async Task<HttpResponseMessage> Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/jobs");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-efmailagent-token,content-type");
        return await _api.App.GetTestClientWithoutToken().SendAsync(request);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}

internal static class ApiTestExtensions
{
    public static HttpClient GetTestClientWithoutToken(this Microsoft.AspNetCore.Builder.WebApplication app) =>
        Microsoft.AspNetCore.TestHost.HostBuilderTestServerExtensions.GetTestClient(app);

    public static EfMailAgent.Rules.RuleRepository GetRequiredServiceRules(this IServiceProvider services) =>
        (EfMailAgent.Rules.RuleRepository)services.GetService(typeof(EfMailAgent.Rules.RuleRepository))!;
}
