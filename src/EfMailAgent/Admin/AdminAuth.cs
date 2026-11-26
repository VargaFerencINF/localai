using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using EfMailAgent.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Admin;

/// <summary>
/// Admin login: the token is exchanged for a session cookie (HttpOnly, SameSite=Strict, path /admin). The cookie value
/// is "issuedAt.signature" with an HMAC-SHA256 over the issue time keyed by the API token, so sessions survive a
/// restart but die when the token changes, and expire after 12 hours.
/// </summary>
public sealed class AdminSessions(AppSettings settings, TimeProvider timeProvider)
{
    public const string CookieName = "efmailagent_admin";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public bool TokenMatches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(settings.Server.Token)));
    }

    public string Issue()
    {
        var issued = timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{issued}.{Sign(issued)}";
    }

    public bool IsValid(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie))
            return false;
        var dot = cookie.IndexOf('.');
        if (dot <= 0 || !long.TryParse(cookie[..dot], out var issued))
            return false;
        var expected = Encoding.ASCII.GetBytes(Sign(cookie[..dot]));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(cookie[(dot + 1)..])))
            return false;
        var age = timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issued);
        return age >= TimeSpan.FromMinutes(-5) && age <= Lifetime;
    }

    public static CookieOptions CookieOptions(bool expire = false) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = false, // plain http on 127.0.0.1
        Path = "/admin",
        IsEssential = true,
        Expires = expire ? DateTimeOffset.UnixEpoch : null,
    };

    private string Sign(string issued) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("efmailagent-admin:" + settings.Server.Token), Encoding.UTF8.GetBytes(issued)));
}

/// <summary>
/// Guards /admin/api/*: a valid session cookie (except for login/session), and for every mutating request the CSRF
/// header X-EfMailAgent-Admin plus a same-origin Origin (or Referer) header. Failures: 401 / 403 without details.
/// </summary>
public sealed class AdminGuardMiddleware(RequestDelegate next, AdminSessions sessions, ILogger<AdminGuardMiddleware> logger)
{
    public const string CsrfHeader = "X-EfMailAgent-Admin";
    public const long MaxBodyBytes = 4 * 1024 * 1024;

    private static readonly string[] Anonymous = ["/admin/api/login", "/admin/api/session"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/admin"))
        {
            await next(context);
            return;
        }

        // Admin pages must never be framed or sniffed.
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

        if (!path.StartsWithSegments("/admin/api"))
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !IsSameOriginWithHeader(context))
        {
            logger.LogWarning("Admin kérés elutasítva (CSRF védelem): {Method} {Path}, Origin: {Origin}", context.Request.Method, path,
                context.Request.Headers.Origin.ToString());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!Anonymous.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase))
            && !sessions.IsValid(context.Request.Cookies[AdminSessions.CookieName]))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    /// <summary>Custom header (forces a CORS preflight for cross-site requests) and Origin/Referer equal to our own origin.</summary>
    public static bool IsSameOriginWithHeader(HttpContext context)
    {
        if (context.Request.Headers[CsrfHeader].ToString() != "1")
            return false;

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            origin = context.Request.Headers.Referer.ToString();
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        var host = context.Request.Host;
        var requestPort = host.Port ?? (context.Request.IsHttps ? 443 : 80);
        return string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase)
               && uri.Port == requestPort;
    }
}
