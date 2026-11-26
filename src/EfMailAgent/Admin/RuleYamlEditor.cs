using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace EfMailAgent.Admin;

/// <summary>
/// Small, comment-preserving text edits of rule.yaml for the admin UI (enabled switch, skills list, id/name when a rule
/// is duplicated). Anything else is edited by the user in the YAML editor itself.
/// </summary>
public static partial class RuleYamlEditor
{
    [GeneratedRegex("^[A-Za-z0-9_./-]+$")]
    private static partial Regex PlainScalar();

    /// <summary>Sets a top-level scalar key (id, name, enabled, priority); inserts it at the top if missing.</summary>
    public static string SetTopLevel(string yaml, string key, string valueLiteral)
    {
        var lines = Lines(yaml);
        var pattern = new Regex($"^{Regex.Escape(key)}\\s*:(?<value>[^#]*)(?<comment>#.*)?$");
        for (var i = 0; i < lines.Count; i++)
        {
            var match = pattern.Match(lines[i]);
            if (!match.Success)
                continue;
            var comment = match.Groups["comment"].Success ? "  " + match.Groups["comment"].Value.Trim() : "";
            lines[i] = $"{key}: {valueLiteral}{comment}";
            return Join(lines);
        }

        var insertAt = key == "id" ? 0 : Math.Max(0, lines.FindIndex(l => l.StartsWith("name:", StringComparison.Ordinal)) + 1);
        lines.Insert(insertAt, $"{key}: {valueLiteral}");
        return Join(lines);
    }

    /// <summary>Replaces (or adds) the top-level skills list with a block sequence.</summary>
    public static string SetSkills(string yaml, IReadOnlyList<string> skills)
    {
        var lines = Lines(yaml);
        var block = skills.Count == 0 ? new List<string> { "skills: []" } : ["skills:", .. skills.Select(s => "  - " + Quote(s))];

        var start = lines.FindIndex(l => Regex.IsMatch(l, @"^skills\s*:"));
        if (start >= 0)
        {
            // The value runs over the following indented, "- item", comment or blank lines; trailing blank/comment lines stay.
            var end = start;
            for (var i = start + 1; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] is ' ' or '\t' or '-' or '#')
                    end = i;
                else
                    break;
            }
            while (end > start && (lines[end].Trim().Length == 0 || lines[end].TrimStart().StartsWith('#')))
                end--;
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, block);
            return Join(lines);
        }

        var after = lines.FindIndex(l => l.StartsWith("prompt_file:", StringComparison.Ordinal));
        if (after < 0)
            after = lines.FindIndex(l => l.StartsWith("output:", StringComparison.Ordinal)) - 1;
        lines.InsertRange(after < 0 ? lines.Count : after + 1, block);
        return Join(lines);
    }

    /// <summary>The skills list as currently written in rule.yaml.</summary>
    public static IReadOnlyList<string> GetSkills(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return [];
        foreach (var (key, value) in root.Children)
        {
            if (key is YamlScalarNode { Value: "skills" } && value is YamlSequenceNode sequence)
                return sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "").Where(s => s.Length > 0).ToArray();
        }
        return [];
    }

    /// <summary>Replaces the old rule id inside the output file pattern (so a duplicated rule writes its own CSV).</summary>
    public static string ReplaceInOutputFile(string yaml, string oldId, string newId)
    {
        var lines = Lines(yaml);
        for (var i = 0; i < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i], @"^\s+file\s*:") && lines[i].Contains(oldId, StringComparison.Ordinal))
                lines[i] = lines[i].Replace(oldId, newId, StringComparison.Ordinal);
        }
        return Join(lines);
    }

    public static string Quote(string value) => PlainScalar().IsMatch(value) ? value : JsonSerializer.Serialize(value);

    /// <summary>Double-quoted YAML string (JSON string syntax is valid YAML), keeps non-ASCII readable.</summary>
    public static string QuoteAlways(string value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static List<string> Lines(string yaml) => yaml.Replace("\r\n", "\n").Split('\n').ToList();

    private static string Join(List<string> lines) => string.Join("\n", lines);
}
