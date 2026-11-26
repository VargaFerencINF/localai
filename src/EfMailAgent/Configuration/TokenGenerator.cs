using System.Buffers.Text;
using System.Security.Cryptography;

namespace EfMailAgent.Configuration;

public static class TokenGenerator
{
    public const int TokenBytes = 32;

    /// <summary>32 cryptographically random bytes, base64url without padding (43 characters).</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
}
