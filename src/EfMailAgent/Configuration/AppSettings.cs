namespace EfMailAgent.Configuration;

/// <summary>Root of appsettings.json. Property names are PascalCase in the file, as is usual for .NET configuration.</summary>
public sealed class AppSettings
{
    public ServerSettings Server { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
    public PathSettings Paths { get; set; } = new();
    public CsvSettings Csv { get; set; } = new();
    public AppGeneralSettings App { get; set; } = new();
    public WorkerSettings Worker { get; set; } = new();
}

public sealed class ServerSettings
{
    public const int DefaultPort = 47811;

    public int Port { get; set; } = DefaultPort;

    /// <summary>Shared secret for the X-EfMailAgent-Token header (32 random bytes, base64url).</summary>
    public string Token { get; set; } = "";
}

public static class LlmProviders
{
    public const string Ollama = "ollama";
    public const string OpenAiCompatible = "openai_compatible";
    public const string Mock = "mock";

    public static readonly IReadOnlyList<string> All = [Ollama, OpenAiCompatible, Mock];
}

public sealed class LlmSettings
{
    public string Provider { get; set; } = LlmProviders.Ollama;
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 300;
    public double Temperature { get; set; }

    /// <summary>Context window for Ollama. Ollama's own default is small and silently truncates long prompts.</summary>
    public int NumCtx { get; set; } = 16384;

    /// <summary>Ollama "think" flag. Off by default: reasoning models may otherwise put the JSON into the thinking field.</summary>
    public bool Think { get; set; }

    /// <summary>Folder with simulated answers for the "mock" provider (see docs/DEV_MODE.md).</summary>
    public string MockDir { get; set; } = "";

    public int MockDelayMs { get; set; } = 1500;
}

/// <summary>Relative paths are resolved against the data directory.</summary>
public sealed class PathSettings
{
    public string Rules { get; set; } = "rules";
    public string Output { get; set; } = "output";
    public string Data { get; set; } = "data";
    public string Logs { get; set; } = "logs";
}

public static class CsvEncodings
{
    public const string Utf8Bom = "utf-8-bom";
    public const string Utf8 = "utf-8";
}

public sealed class CsvSettings
{
    public string Delimiter { get; set; } = ";";
    public string DecimalSeparator { get; set; } = ",";
    public string Encoding { get; set; } = CsvEncodings.Utf8Bom;

    /// <summary>How long to keep retrying when the CSV file is locked (e.g. open in Excel) before writing a _pending_ file.</summary>
    public int LockRetrySeconds { get; set; } = 600;

    public int LockRetryIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Text cells starting with = + - @ (or tab / CR) get a leading apostrophe so Excel never runs them as formulas
    /// (CSV injection: subjects and extracted texts come from outside). Number columns are not affected.
    /// </summary>
    public bool EscapeFormulas { get; set; } = true;
}

public sealed class AppGeneralSettings
{
    /// <summary>IANA or Windows time zone id used for every local timestamp we write (CSV, prompts).</summary>
    public string TimeZone { get; set; } = "Europe/Budapest";
}

public sealed class WorkerSettings
{
    public static readonly int[] DefaultRetryDelaysSeconds = [30, 120, 600];

    /// <summary>
    /// Backoff before each retry after a transport error. One initial attempt plus one retry per entry
    /// (default: 30 s, 2 min, 10 min, i.e. 4 attempts in total). Null or empty means the default.
    /// </summary>
    public int[]? RetryDelaysSeconds { get; set; }

    /// <summary>How long the worker pauses when the LLM server is unreachable; such pauses do not use up attempts.</summary>
    public int LlmOutagePauseSeconds { get; set; } = 30;

    /// <summary>Safety poll interval while the queue is empty (new jobs also wake the worker immediately).</summary>
    public int IdlePollMilliseconds { get; set; } = 2000;

    public IReadOnlyList<TimeSpan> EffectiveRetryDelays =>
        (RetryDelaysSeconds is { Length: > 0 } ? RetryDelaysSeconds : DefaultRetryDelaysSeconds)
        .Select(s => TimeSpan.FromSeconds(s)).ToArray();

    public int MaxAttempts => EffectiveRetryDelays.Count + 1;
}
