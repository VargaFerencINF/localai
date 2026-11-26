using System.Net;
using EfMailAgent.Admin;
using EfMailAgent.Configuration;
using EfMailAgent.Tests.Api;
using EfMailAgent.Tests.Fakes;

namespace EfMailAgent.Tests.Admin;

public class AdminAuthTests : IAsyncLifetime
{
    private ApiFixture _api = null!;
    private AdminTestClient _admin = null!;

    public async Task InitializeAsync()
    {
        _api = await ApiFixture.StartAsync();
        _admin = new AdminTestClient(_api);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Static_ui_is_served_with_security_headers()
    {
        var page = await _admin.SendAsync(HttpMethod.Get, "/admin/", withCookie: false);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<html lang=\"hu\">", await page.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());

        Assert.Equal(HttpStatusCode.Redirect, (await _admin.SendAsync(HttpMethod.Get, "/admin", withCookie: false)).StatusCode);
        var js = await _admin.SendAsync(HttpMethod.Get, "/admin/app.js", withCookie: false);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await _admin.SendAsync(HttpMethod.Get, "/admin/app.css", withCookie: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.SendAsync(HttpMethod.Get, "/admin/nincs.js", withCookie: false)).StatusCode);
    }

    [Fact]
    public async Task Api_requires_login_and_login_sets_a_strict_httponly_cookie()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _admin.SendAsync(HttpMethod.Get, "/admin/api/overview")).StatusCode);
        var session = await _admin.GetJsonAsync("/admin/api/session");
        Assert.False(session["authenticated"]!.GetValue<bool>());

        var wrong = await _admin.SendAsync(HttpMethod.Post, "/admin/api/login", new { token = "rossz" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("Hibás token", await wrong.Content.ReadAsStringAsync());

        var ok = await _admin.SendAsync(HttpMethod.Post, "/admin/api/login", new { token = _api.Settings.Settings.Server.Token });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cookie = ok.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/admin", cookie);

        Assert.Equal(HttpStatusCode.OK, (await _admin.SendAsync(HttpMethod.Get, "/admin/api/overview")).StatusCode);
        Assert.True((await _admin.GetJsonAsync("/admin/api/session"))["authenticated"]!.GetValue<bool>());

        // The API token itself is not a session: a forged cookie value is rejected.
        _admin.Cookie = _api.Settings.Settings.Server.Token;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _admin.SendAsync(HttpMethod.Get, "/admin/api/overview")).StatusCode);
    }

    [Fact]
    public async Task Logout_clears_the_cookie()
    {
        await _admin.LoginAsync();
        await _admin.SendAsync(HttpMethod.Post, "/admin/api/logout");
        Assert.Null(_admin.Cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _admin.SendAsync(HttpMethod.Get, "/admin/api/rules")).StatusCode);
    }

    [Theory]
    [InlineData(false, "http://localhost", null)] // no custom header
    [InlineData(true, "http://evil.example", null)] // foreign origin
    [InlineData(true, "http://localhost:8080", null)] // other port
    [InlineData(true, null, null)] // neither Origin nor Referer
    public async Task Mutating_requests_without_csrf_protection_are_rejected(bool header, string? origin, string? referer)
    {
        await _admin.LoginAsync();

        var response = await _admin.SendAsync(HttpMethod.Post, "/admin/api/rules/eloreendeles/enabled", new { enabled = false }, header, origin, referer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(_api.App.Services.GetRequiredServiceRules().Current.FindById("eloreendeles")!.Enabled);
    }

    [Fact]
    public async Task Same_origin_referer_is_accepted_when_origin_is_missing()
    {
        await _admin.LoginAsync();
        var response = await _admin.SendAsync(HttpMethod.Post, "/admin/api/rules/eloreendeles/enabled", new { enabled = true }, true, origin: null,
            referer: "http://localhost/admin/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_itself_needs_the_csrf_header()
    {
        var response = await _admin.SendAsync(HttpMethod.Post, "/admin/api/login", new { token = _api.Settings.Settings.Server.Token }, csrfHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void Sessions_expire_and_die_with_a_new_token()
    {
        var settings = new AppSettings { Server = { Token = TokenGenerator.NewToken() } };
        var clock = new TestClock();
        var sessions = new AdminSessions(settings, clock);
        var cookie = sessions.Issue();

        Assert.True(sessions.IsValid(cookie));
        Assert.False(sessions.IsValid(cookie + "x"));
        Assert.False(sessions.IsValid("123.abc"));
        clock.Advance(TimeSpan.FromHours(13));
        Assert.False(sessions.IsValid(cookie));

        var fresh = sessions.Issue();
        settings.Server.Token = TokenGenerator.NewToken();
        Assert.False(sessions.IsValid(fresh));
    }
}
