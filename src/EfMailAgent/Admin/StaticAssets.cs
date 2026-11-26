using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EfMailAgent.Admin;

/// <summary>Serves the admin UI from embedded resources (no files on disk, no CDN, no build step).</summary>
public static class StaticAssets
{
    private const string Prefix = "admin/";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".json"] = "application/json; charset=utf-8",
    };

    public static IReadOnlyCollection<string> Names { get; } = typeof(StaticAssets).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Select(n => n[Prefix.Length..]).ToArray();

    public static IEndpointRouteBuilder MapAdminStaticAssets(this IEndpointRouteBuilder app)
    {
        // Routing treats /admin and /admin/ as one route; relative asset URLs need the trailing slash.
        app.MapGet("/admin", (HttpContext context) => context.Request.Path.Value!.EndsWith('/')
            ? Serve(context, "index.html")
            : Results.Redirect("/admin/")).ExcludeFromDescription();
        app.MapGet("/admin/{file}", (HttpContext context, string file) => Serve(context, file)).ExcludeFromDescription();
        return app;
    }

    private static IResult Serve(HttpContext context, string file)
    {
        if (!Names.Contains(file))
            return Results.NotFound();
        var stream = typeof(StaticAssets).Assembly.GetManifestResourceStream(Prefix + file)!;
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Stream(stream, ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream"));
    }
}
