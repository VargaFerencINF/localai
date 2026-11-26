using System.Text.Json;
using EfMailAgent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Addon;

/// <summary>On every service start: verify the add-on policy entry and repair it if needed (runs in the background).</summary>
public sealed class AddonSelfHealService(EffectiveSettings settings, ILogger<AddonInstaller> logger) : IHostedService
{
    public Task? Running { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Running = Task.Run(() => CreateInstaller(settings, logger).SelfHeal(), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The locator also checks the folders recorded by install-addon (e.g. a --thunderbird-dir install).</summary>
    public static AddonInstaller CreateInstaller(EffectiveSettings settings, ILogger<AddonInstaller>? logger = null)
    {
        var known = new List<string>();
        var statePath = Path.Combine(settings.AddonDir, AddonInstaller.StateFileName);
        try
        {
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(statePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                known.AddRange(state?.ThunderbirdDirs ?? []);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // an unreadable state file means: behave as if install-addon never ran
        }
        return new AddonInstaller(settings, new CombinedThunderbirdLocator(known, ThunderbirdLocator.CreateDefault()), logger: logger);
    }
}
