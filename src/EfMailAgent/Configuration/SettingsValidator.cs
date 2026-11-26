namespace EfMailAgent.Configuration;

public static class SettingsValidator
{
    /// <summary>Returns Hungarian error messages; empty when the settings are usable.</summary>
    public static IReadOnlyList<string> Validate(AppSettings s)
    {
        var errors = new List<string>();

        if (s.Server.Port is < 1 or > 65535)
            errors.Add($"Server.Port érvénytelen: {s.Server.Port} (1 és 65535 közötti szám kell).");
        if (string.IsNullOrWhiteSpace(s.Server.Token) || s.Server.Token.Length < 32)
            errors.Add("Server.Token hiányzik vagy túl rövid (legalább 32 karakter kell).");

        if (!LlmProviders.All.Contains(s.Llm.Provider))
            errors.Add($"Llm.Provider ismeretlen: '{s.Llm.Provider}' (megengedett: {string.Join(", ", LlmProviders.All)}).");
        if (s.Llm.Provider != LlmProviders.Mock
            && !Uri.TryCreate(s.Llm.BaseUrl, UriKind.Absolute, out _))
            errors.Add($"Llm.BaseUrl érvénytelen URL: '{s.Llm.BaseUrl}'.");
        if (s.Llm.TimeoutSeconds is < 1 or > 3600)
            errors.Add($"Llm.TimeoutSeconds érvénytelen: {s.Llm.TimeoutSeconds} (1 és 3600 közötti szám kell).");
        if (s.Llm.Temperature is < 0 or > 2)
            errors.Add($"Llm.Temperature érvénytelen: {s.Llm.Temperature} (0 és 2 közötti szám kell).");
        if (s.Llm.NumCtx < 0)
            errors.Add($"Llm.NumCtx nem lehet negatív: {s.Llm.NumCtx}.");
        if (s.Llm.Provider == LlmProviders.Mock && string.IsNullOrWhiteSpace(s.Llm.MockDir))
            errors.Add("Llm.MockDir megadása kötelező a 'mock' szolgáltatónál.");
        if (s.Llm.MockDelayMs < 0)
            errors.Add($"Llm.MockDelayMs nem lehet negatív: {s.Llm.MockDelayMs}.");

        if (s.Csv.Delimiter.Length != 1 || s.Csv.Delimiter is "\"" or "\r" or "\n")
            errors.Add($"Csv.Delimiter egyetlen karakter legyen (nem idézőjel vagy sortörés): '{s.Csv.Delimiter}'.");
        if (s.Csv.DecimalSeparator is not ("," or "."))
            errors.Add($"Csv.DecimalSeparator csak ',' vagy '.' lehet: '{s.Csv.DecimalSeparator}'.");
        if (s.Csv.DecimalSeparator == s.Csv.Delimiter)
            errors.Add("Csv.DecimalSeparator nem egyezhet meg a Csv.Delimiter értékével.");
        if (s.Csv.Encoding is not (CsvEncodings.Utf8Bom or CsvEncodings.Utf8))
            errors.Add($"Csv.Encoding ismeretlen: '{s.Csv.Encoding}' (megengedett: {CsvEncodings.Utf8Bom}, {CsvEncodings.Utf8}).");

        if (s.Csv.LockRetrySeconds < 0)
            errors.Add($"Csv.LockRetrySeconds nem lehet negatív: {s.Csv.LockRetrySeconds}.");
        if (s.Csv.LockRetryIntervalSeconds < 1)
            errors.Add($"Csv.LockRetryIntervalSeconds legalább 1 legyen: {s.Csv.LockRetryIntervalSeconds}.");

        if (s.Worker.RetryDelaysSeconds is { } delays && delays.Any(d => d < 0))
            errors.Add("Worker.RetryDelaysSeconds nem tartalmazhat negatív értéket.");
        if (s.Worker.LlmOutagePauseSeconds < 1)
            errors.Add($"Worker.LlmOutagePauseSeconds legalább 1 legyen: {s.Worker.LlmOutagePauseSeconds}.");
        if (s.Worker.IdlePollMilliseconds < 50)
            errors.Add($"Worker.IdlePollMilliseconds legalább 50 legyen: {s.Worker.IdlePollMilliseconds}.");

        foreach (var (name, value) in new[]
                 {
                     ("Paths.Rules", s.Paths.Rules), ("Paths.Output", s.Paths.Output),
                     ("Paths.Data", s.Paths.Data), ("Paths.Logs", s.Paths.Logs),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"{name} nem lehet üres.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(s.App.TimeZone);
        }
        catch (Exception)
        {
            errors.Add($"App.TimeZone ismeretlen időzóna: '{s.App.TimeZone}'.");
        }

        return errors;
    }
}
