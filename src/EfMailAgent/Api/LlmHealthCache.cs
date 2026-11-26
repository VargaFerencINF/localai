using EfMailAgent.Llm;

namespace EfMailAgent.Api;

/// <summary>LLM reachability for /api/health, cached for 30 seconds so polling clients do not hammer the LLM server.</summary>
public sealed class LlmHealthCache(ILlmClient llm, TimeProvider timeProvider)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (LlmHealth Health, DateTimeOffset At)? _cached;

    public async Task<LlmHealth> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } c && timeProvider.GetUtcNow() - c.At < Ttl)
            return c.Health;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } again && timeProvider.GetUtcNow() - again.At < Ttl)
                return again.Health;
            var health = await llm.CheckHealthAsync(cancellationToken);
            _cached = (health, timeProvider.GetUtcNow());
            return health;
        }
        finally
        {
            _lock.Release();
        }
    }
}
