using EfMailAgent.Configuration;

namespace EfMailAgent.Llm;

/// <summary>
/// Wraps the configured client and recreates it when the provider, base URL or mock settings change (admin UI), so
/// LLM settings apply without a restart. Model, temperature, timeout and num_ctx are read per request anyway.
/// </summary>
public sealed class ReloadableLlmClient(EffectiveSettings settings) : ILlmClient
{
    private readonly object _lock = new();
    private (string Provider, string BaseUrl, string MockDir, int MockDelayMs) _key;
    private ILlmClient? _current;

    public string Provider => Current.Provider;

    public ILlmClient Current
    {
        get
        {
            var llm = settings.Settings.Llm;
            var key = (llm.Provider, llm.BaseUrl, llm.MockDir, llm.MockDelayMs);
            lock (_lock)
            {
                if (_current is null || key != _key)
                {
                    _current = LlmClientFactory.Create(settings);
                    _key = key;
                }
                return _current;
            }
        }
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
        Current.CompleteAsync(request, cancellationToken);

    public Task<LlmHealth> CheckHealthAsync(CancellationToken cancellationToken) => Current.CheckHealthAsync(cancellationToken);
}
