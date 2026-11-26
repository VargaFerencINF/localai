using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EfMailAgent.Admin;

/// <summary>
/// One-time login links for the admin UI, so that starting the program opens the UI already logged in (no token to
/// type). A code is "issuedAt.nonce.signature" with an HMAC-SHA256 keyed by the API token: anyone who can create one
/// can read the token anyway (the exe itself, or open-admin on the same machine). Valid for 2 minutes, usable once.
/// </summary>
public static class LoginLink
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(2);

    /// <summary>Pages a link may open (the hash routes of the admin UI).</summary>
    public static readonly IReadOnlySet<string> Pages = new HashSet<string>(StringComparer.Ordinal)
    {
        "attekintes", "szabalyok", "tesztkonzol", "teljesitmeny", "beallitasok", "naplo",
    };

    public static string CreateCode(string token, DateTimeOffset now)
    {
        var payload = $"{now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}.{Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12))}";
        return $"{payload}.{Sign(token, payload)}";
    }

    public static string CreateUrl(int port, string token, DateTimeOffset now, string? page = null)
    {
        var url = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/admin/login?code={Uri.EscapeDataString(CreateCode(token, now))}";
        return page is not null && Pages.Contains(page) ? $"{url}&page={page}" : url;
    }

    /// <summary>Checks signature and age only (single use is enforced by <see cref="LoginCodeRegistry"/>).</summary>
    public static bool IsAuthentic(string token, string? code, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 200)
            return false;
        var lastDot = code.LastIndexOf('.');
        var firstDot = code.IndexOf('.');
        if (firstDot <= 0 || lastDot <= firstDot || !long.TryParse(code[..firstDot], NumberStyles.None, CultureInfo.InvariantCulture, out var issued))
            return false;
        var expected = Encoding.ASCII.GetBytes(Sign(token, code[..lastDot]));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(code[(lastDot + 1)..])))
            return false;
        var age = now - DateTimeOffset.FromUnixTimeSeconds(issued);
        return age >= TimeSpan.FromMinutes(-1) && age <= Validity;
    }

    private static string Sign(string token, string payload) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("efmailagent-login-link:" + token), Encoding.UTF8.GetBytes(payload)));
}

/// <summary>Remembers used login codes until they expire, so a link works only once.</summary>
public sealed class LoginCodeRegistry(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    public bool TryConsume(string token, string? code)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (key, usedAt) in _used)
        {
            if (now - usedAt > LoginLink.Validity + TimeSpan.FromMinutes(2))
                _used.TryRemove(key, out _);
        }
        return LoginLink.IsAuthentic(token, code, now) && _used.TryAdd(code!, now);
    }
}
