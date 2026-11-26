using System.Security.Cryptography;
using System.Text;
using EfMailAgent.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Api;

/// <summary>
/// Protects /api/*: request size limit (413) and the shared token in X-EfMailAgent-Token, compared in constant time
/// (401 with an empty body). CORS preflight (OPTIONS) is answered by the CORS middleware before this runs.
/// </summary>
public sealed class ApiGuardMiddleware(RequestDelegate next, AppSettings settings, ILogger<ApiGuardMiddleware> logger)
{
    public const string TokenHeader = "X-EfMailAgent-Token";
    public const long MaxBodyBytes = 2 * 1024 * 1024;

    private readonly byte[] _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Server.Token));

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api") || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        // Debug only: lets the operator confirm which Origin Thunderbird sends (docs/DECISIONS.md D-303).
        logger.LogDebug("API kérés: {Method} {Path}, Origin: {Origin}", context.Request.Method, context.Request.Path,
            context.Request.Headers.Origin.ToString());

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var presented = context.Request.Headers[TokenHeader].ToString();
        // Hashing first makes the comparison constant-time regardless of the presented length.
        if (presented.Length == 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), _tokenHash))
        {
            logger.LogWarning("Elutasított API kérés (hiányzó vagy hibás token): {Method} {Path}, Origin: {Origin}",
                context.Request.Method, context.Request.Path, context.Request.Headers.Origin.ToString());
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
