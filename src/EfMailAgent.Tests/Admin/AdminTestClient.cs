using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EfMailAgent.Admin;
using EfMailAgent.Tests.Api;

namespace EfMailAgent.Tests.Admin;

/// <summary>Talks to /admin/api like the browser does: session cookie, CSRF header and same-origin Origin header.</summary>
public sealed class AdminTestClient(ApiFixture api)
{
    public string? Cookie { get; set; }

    public async Task LoginAsync()
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/api/login", new { token = api.Settings.Settings.Server.Token });
        response.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool csrfHeader = true,
        string? origin = "http://localhost", string? referer = null, bool withCookie = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (csrfHeader)
            request.Headers.Add(AdminGuardMiddleware.CsrfHeader, "1");
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (referer is not null)
            request.Headers.Referrer = new Uri(referer);
        if (withCookie && Cookie is not null)
            request.Headers.Add("Cookie", $"{AdminSessions.CookieName}={Cookie}");

        var client = Microsoft.AspNetCore.TestHost.HostBuilderTestServerExtensions.GetTestClient(api.App);
        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies.Where(c => c.StartsWith(AdminSessions.CookieName + "=", StringComparison.Ordinal)))
            {
                var value = cookie.Split(';')[0][(AdminSessions.CookieName.Length + 1)..];
                Cookie = value.Length == 0 ? null : value;
            }
        }
        return response;
    }

    public async Task<JsonNode> GetJsonAsync(string path)
    {
        var response = await SendAsync(HttpMethod.Get, path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    public async Task<(int Status, JsonNode? Body)> JsonAsync(HttpMethod method, string path, object? body = null)
    {
        var response = await SendAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }
}
