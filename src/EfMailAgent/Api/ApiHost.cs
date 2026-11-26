using System.Net;
using EfMailAgent.Configuration;
using EfMailAgent.Hosting;
using EfMailAgent.Rules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Serilog;

namespace EfMailAgent.Api;

/// <summary>Builds the long-running host: worker + local HTTP API on 127.0.0.1 only.</summary>
public static class ApiHost
{
    public const string OpenApiPath = "/api/openapi.json";

    public static WebApplication Build(EffectiveSettings settings, Serilog.ILogger serilog, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });

        // Started by the Service Control Manager: Windows Service lifetime (no-op when run from a console).
        builder.Services.AddWindowsService(options => options.ServiceName = Service.ServiceManager.ServiceName);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(serilog, dispose: false);

        builder.WebHost.ConfigureKestrel(options =>
        {
            // Loopback only: the add-on runs on the same machine; nothing else may reach the API.
            options.Listen(IPAddress.Loopback, settings.Settings.Server.Port);
            options.Limits.MaxRequestBodySize = ApiGuardMiddleware.MaxBodyBytes;
            options.AddServerHeader = false;
        });

        builder.Services.AddEfMailAgentCore(settings);
        builder.Services.AddEfMailAgentWorker();
        builder.Services.AddSingleton<LlmHealthCache>();
        builder.Services.AddSingleton(new Hosting.RuntimeInfo { IsWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() });
        builder.Services.AddSingleton<Admin.AdminSessions>();
        builder.Services.AddSingleton<Admin.LoginCodeRegistry>();
        builder.Services.AddSingleton<Admin.RuleFileService>();
        builder.Services.AddSingleton<Admin.SettingsService>();
        builder.Services.AddSingleton(new Admin.OllamaSetupOptions());
        builder.Services.AddSingleton<Admin.IOllamaPlatform>(_ => OperatingSystem.IsWindows() ? new Admin.WindowsOllamaPlatform() : new Admin.UnsupportedOllamaPlatform());
        builder.Services.AddSingleton<Admin.OllamaSetupService>();
        builder.Services.AddSingleton<Addon.IElevationCheck, Addon.SystemElevationCheck>();
        builder.Services.AddSingleton<Admin.IElevatedRunner, Admin.WindowsElevatedRunner>();
        builder.Services.AddSingleton(sp => new Admin.AddonSetupService(sp.GetRequiredService<Configuration.EffectiveSettings>(),
            sp.GetRequiredService<Addon.IElevationCheck>(), sp.GetRequiredService<Admin.IElevatedRunner>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Admin.AddonSetupService>>()));
        builder.Services.AddSingleton<Addon.AddonSelfHealService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Addon.AddonSelfHealService>());
        builder.Services.Configure<JsonOptions>(o =>
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        builder.Services.AddCors(options => options.AddPolicy(ApiEndpoints.CorsPolicy, policy => policy
            .SetIsOriginAllowed(IsExtensionOrigin)
            .WithMethods("GET", "POST")
            .WithHeaders(ApiGuardMiddleware.TokenHeader, "Content-Type", "If-None-Match")
            .WithExposedHeaders("ETag")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "EfMailAgent API",
                    Version = VersionInfo.Current,
                    Description = "Helyi API a Thunderbird add-on számára. Minden /api/* hívás az X-EfMailAgent-Token fejlécet igényli.",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["token"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiGuardMiddleware.TokenHeader,
                };
                return Task.CompletedTask;
            });
        });

        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseRouting();
        app.UseCors();
        app.UseMiddleware<ApiGuardMiddleware>();
        app.UseMiddleware<Admin.AdminGuardMiddleware>();
        app.MapEfMailAgentApi();
        Admin.AdminEndpoints.MapEfMailAgentAdmin(app);
        app.MapOpenApi(OpenApiPath).RequireCors(ApiEndpoints.CorsPolicy);

        // Load rules and start the file watcher before the first request arrives.
        app.Services.GetRequiredService<RuleRepository>();
        return app;
    }

    /// <summary>Thunderbird sends Origin: moz-extension://&lt;per-profile UUID&gt; for requests of the add-on.</summary>
    public static bool IsExtensionOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "moz-extension" && uri.Host.Length > 0;
}
