using EfMailAgent.Configuration;

namespace EfMailAgent.Llm;

public static class LlmClientFactory
{
    public static ILlmClient Create(EffectiveSettings settings, HttpClient? httpClient = null)
    {
        var llm = settings.Settings.Llm;
        httpClient ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return llm.Provider switch
        {
            LlmProviders.Ollama => new OllamaClient(httpClient, llm.BaseUrl),
            LlmProviders.OpenAiCompatible => new OpenAiCompatibleClient(httpClient, llm.BaseUrl),
            LlmProviders.Mock => new MockLlmClient(llm.MockDir, llm.MockDelayMs, Path.Combine(settings.DataDir, "mock-prompts")),
            _ => throw new InvalidOperationException($"Ismeretlen LLM szolgáltató: {llm.Provider}"),
        };
    }
}
