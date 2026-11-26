using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using EfMailAgent.Llm;
using EfMailAgent.Output;
using EfMailAgent.Processing;
using EfMailAgent.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Hosting;

public static class ServiceRegistration
{
    /// <summary>Registers the processing pipeline. The HTTP host (Step 3) and the CLI share this composition.</summary>
    public static IServiceCollection AddEfMailAgentCore(this IServiceCollection services, EffectiveSettings settings,
        bool startRuleWatcher = true)
    {
        services.AddSingleton(settings);
        services.AddSingleton(settings.Settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(TimeZoneInfo.FindSystemTimeZoneById(settings.Settings.App.TimeZone));

        services.AddSingleton(sp =>
        {
            var repository = new RuleRepository(settings.RulesDir, sp.GetRequiredService<ILogger<RuleRepository>>());
            if (startRuleWatcher)
                repository.Start();
            else
                repository.Reload(force: true);
            return repository;
        });
        services.AddSingleton(_ => new JobDatabase(settings.DataDir));
        services.AddSingleton(sp => new JobQueue(sp.GetRequiredService<JobDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ILlmClient>(_ => LlmClientFactory.Create(settings));
        services.AddSingleton(sp => new CsvWriter(settings.Settings.Csv, settings.OutputDir, sp.GetRequiredService<TimeZoneInfo>(),
            sp.GetRequiredService<ILogger<CsvWriter>>()));
        services.AddSingleton(sp => new JobProcessor(
            sp.GetRequiredService<RuleRepository>(),
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<ILlmClient>(),
            sp.GetRequiredService<CsvWriter>(),
            settings,
            sp.GetRequiredService<TimeZoneInfo>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<JobProcessor>>()));
        services.AddSingleton(sp => new JobWorker(sp.GetRequiredService<JobQueue>(), sp.GetRequiredService<JobProcessor>(), settings,
            sp.GetRequiredService<ILogger<JobWorker>>()));
        return services;
    }

    /// <summary>Adds the worker as a hosted service (service / console host).</summary>
    public static IServiceCollection AddEfMailAgentWorker(this IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());
}
