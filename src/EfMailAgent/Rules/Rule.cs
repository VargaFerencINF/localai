namespace EfMailAgent.Rules;

public enum ColumnType
{
    String,
    Number,
    Integer,
    Date,
    Boolean,
    Enum,
}

public enum OutputMode
{
    Append,
    PerJob,
}

public sealed record RuleColumn(string Name, ColumnType Type, bool Required, string Description, IReadOnlyList<string> Values);

public sealed record RuleSkill(string RelativePath, string FileName, string Content);

public sealed record RuleLlmOverrides(string? Model, double? Temperature, int MaxBodyChars);

public sealed record RuleOutput(OutputMode Mode, string FilePattern, string RowsPath, IReadOnlyList<RuleColumn> Columns);

/// <summary>A validated, fully loaded rule. Prompt and skill texts are read at load time so a reload is atomic.</summary>
public sealed record Rule(
    string Id,
    string Name,
    bool Enabled,
    int Priority,
    IReadOnlyList<string> SubjectContains,
    string? SubjectRegex,
    string FolderPath,
    string PromptFile,
    string PromptText,
    IReadOnlyList<RuleSkill> Skills,
    RuleOutput Output,
    RuleLlmOverrides Llm)
{
    public const int DefaultMaxBodyChars = 20000;

    /// <summary>Precomputed matcher (normalized keywords, compiled regex).</summary>
    public required Matching.SubjectMatcher Matcher { get; init; }
}

public sealed record RuleIssue(string Folder, string Message, bool IsWarning = false)
{
    public override string ToString() => IsWarning ? $"[figyelmeztetés] {Message}" : Message;
}
