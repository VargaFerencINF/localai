using System.Net;
using EfMailAgent.Admin;
using EfMailAgent.Tests.Api;
using EfMailAgent.Tests.Fakes;

namespace EfMailAgent.Tests.Admin;

public class LoginLinkTests : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.StartAsync();

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private string Token => _api.Settings.Settings.Server.Token;

    [Fact]
    public void Code_is_valid_for_two_minutes_with_the_right_token_only_and_only_once()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-26T10:00:00Z"));
        var code = LoginLink.CreateCode("token-a", clock.GetUtcNow());

        Assert.True(LoginLink.IsAuthentic("token-a", code, clock.GetUtcNow().AddSeconds(119)));
        Assert.False(LoginLink.IsAuthentic("token-a", code, clock.GetUtcNow().AddSeconds(121)));
        Assert.False(LoginLink.IsAuthentic("token-b", code, clock.GetUtcNow()));
        Assert.False(LoginLink.IsAuthentic("token-a", code[..^2] + "xx", clock.GetUtcNow()));
        Assert.False(LoginLink.IsAuthentic("token-a", "1." + code, clock.GetUtcNow()));
        Assert.False(LoginLink.IsAuthentic("token-a", null, clock.GetUtcNow()));
        Assert.NotEqual(code, LoginLink.CreateCode("token-a", clock.GetUtcNow())); // random nonce

        var registry = new LoginCodeRegistry(clock);
        Assert.True(registry.TryConsume("token-a", code));
        Assert.False(registry.TryConsume("token-a", code));
    }

    [Fact]
    public void Url_points_to_loopback_and_only_known_pages()
    {
        var url = LoginLink.CreateUrl(47811, "t", DateTimeOffset.UtcNow, "beallitasok");
        Assert.StartsWith("http://127.0.0.1:47811/admin/login?code=", url);
        Assert.EndsWith("&page=beallitasok", url);
        Assert.DoesNotContain("page=", LoginLink.CreateUrl(47811, "t", DateTimeOffset.UtcNow, "javascript:alert(1)"));
    }

    [Fact]
    public async Task Link_logs_in_and_opens_the_requested_page_once()
    {
        var admin = new AdminTestClient(_api);
        var code = Uri.EscapeDataString(LoginLink.CreateCode(Token, DateTimeOffset.UtcNow));

        var response = await admin.SendAsync(HttpMethod.Get, $"/admin/login?code={code}&page=beallitasok", csrfHeader: false, origin: null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/#/beallitasok", response.Headers.Location!.OriginalString);
        Assert.NotNull(admin.Cookie);
        Assert.True((await admin.GetJsonAsync("/admin/api/session"))["authenticated"]!.GetValue<bool>());

        var again = new AdminTestClient(_api);
        var reused = await again.SendAsync(HttpMethod.Get, $"/admin/login?code={code}", csrfHeader: false, origin: null);
        Assert.Equal("/admin/?link=lejart", reused.Headers.Location!.OriginalString);
        Assert.Null(again.Cookie);
    }

    [Fact]
    public async Task Forged_or_foreign_link_does_not_log_in()
    {
        foreach (var code in new[] { "", "123.abc.def", LoginLink.CreateCode("other-token", DateTimeOffset.UtcNow) })
        {
            var admin = new AdminTestClient(_api);
            var response = await admin.SendAsync(HttpMethod.Get, $"/admin/login?code={Uri.EscapeDataString(code)}", csrfHeader: false, origin: null);
            Assert.Equal("/admin/?link=lejart", response.Headers.Location!.OriginalString);
            Assert.Null(admin.Cookie);
        }
    }
}
