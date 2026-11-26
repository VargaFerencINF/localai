namespace EfMailAgent.Rules;

// YAML data transfer objects (rule.yaml). Mapped with the underscored naming convention, e.g. prompt_file.
// Everything is nullable so that missing fields produce Hungarian validation errors instead of exceptions.

public sealed class RuleDocument
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public int? Priority { get; set; }
    public MatchDocument? Match { get; set; }
    public string? PromptFile { get; set; }
    public List<string>? Skills { get; set; }
    public OutputDocument? Output { get; set; }
    public LlmOverrideDocument? Llm { get; set; }
}

public sealed class MatchDocument
{
    public List<string>? SubjectContains { get; set; }
    public string? SubjectRegex { get; set; }
}

public sealed class OutputDocument
{
    public string? Mode { get; set; }
    public string? File { get; set; }
    public string? RowsPath { get; set; }
    public List<ColumnDocument>? Columns { get; set; }
}

public sealed class ColumnDocument
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool? Required { get; set; }
    public string? Description { get; set; }
    public List<string>? Values { get; set; }
}

public sealed class LlmOverrideDocument
{
    public string? Model { get; set; }
    public double? Temperature { get; set; }
    public int? MaxBodyChars { get; set; }
}
