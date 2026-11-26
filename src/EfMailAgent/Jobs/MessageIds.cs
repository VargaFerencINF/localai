namespace EfMailAgent.Jobs;

public static class MessageIds
{
    /// <summary>
    /// Canonical Message-ID form used for storage, dedup and CSV: trimmed and wrapped in angle brackets.
    /// Thunderbird's headerMessageId comes without brackets, raw headers with them; both map to the same value.
    /// </summary>
    public static string Canonicalize(string messageId)
    {
        var id = messageId.Trim();
        if (id.StartsWith('<') && id.EndsWith('>'))
            id = id[1..^1].Trim();
        return id.Length == 0 ? "" : $"<{id}>";
    }

    /// <summary>Used when an email has no Message-ID (e.g. a pasted .txt in the CLI).</summary>
    public static string Generate(string hint = "generated") => $"<{hint}-{Guid.NewGuid():N}@efmailagent.local>";
}
