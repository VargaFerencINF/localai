using EfMailAgent.Admin;

namespace EfMailAgent.Tests.Admin;

public class RuleYamlEditorTests
{
    private const string Yaml = """
        id: x
        name: X   # név
        enabled: true            # megjegyzés marad
        prompt_file: prompt.md
        skills:                  # lista
          - skills/a.md
          # régi
          - skills/b.md
        output:
          file: "x_{yyyy}.csv"
        """;

    [Fact]
    public void Block_skills_list_is_replaced_and_the_rest_kept()
    {
        var result = RuleYamlEditor.SetSkills(Yaml, ["skills/a.md", "skills/c d.md"]);

        Assert.Equal(["skills/a.md", "skills/c d.md"], RuleYamlEditor.GetSkills(result));
        Assert.Contains("  - \"skills/c d.md\"", result);
        Assert.Contains("output:\n  file: \"x_{yyyy}.csv\"", result.Replace("\r\n", "\n"));
        Assert.Contains("enabled: true            # megjegyzés marad", result);
    }

    [Theory]
    [InlineData("id: x\nskills: [skills/a.md, skills/b.md]\noutput:\n  file: a.csv\n")]
    [InlineData("id: x\nskills: []\noutput:\n  file: a.csv\n")]
    [InlineData("id: x\nprompt_file: prompt.md\noutput:\n  file: a.csv\n")]
    public void Flow_empty_or_missing_skills_lists_are_handled(string yaml)
    {
        var result = RuleYamlEditor.SetSkills(yaml, ["skills/z.md"]);
        Assert.Equal(["skills/z.md"], RuleYamlEditor.GetSkills(result));
        Assert.Contains("output:\n  file: a.csv", result);
        Assert.Equal([], RuleYamlEditor.GetSkills(RuleYamlEditor.SetSkills(result, [])));
    }

    [Fact]
    public void Top_level_values_keep_comments_and_are_inserted_when_missing()
    {
        var result = RuleYamlEditor.SetTopLevel(Yaml, "enabled", "false");
        Assert.Contains("enabled: false  # megjegyzés marad", result);
        Assert.Contains("name: \"Új név\"", RuleYamlEditor.SetTopLevel(result, "name", RuleYamlEditor.QuoteAlways("Új név")));
        var inserted = RuleYamlEditor.SetTopLevel("id: x\nname: X\noutput: {}\n", "enabled", "false");
        Assert.StartsWith("id: x\nname: X\nenabled: false\n", inserted);
        Assert.Contains("file: \"y_{yyyy}.csv\"", RuleYamlEditor.ReplaceInOutputFile(Yaml, "x", "y").Replace("x_", "x_"));
    }

    [Fact]
    public void SetOutputScalar_changes_mode_and_file_under_output_only()
    {
        var yaml = File.ReadAllText(Path.Combine(TestPaths.SampleRules, "eloreendeles", "rule.yaml"));

        var changed = RuleYamlEditor.SetOutputScalar(yaml, "mode", "per_job");
        changed = RuleYamlEditor.SetOutputScalar(changed, "file", RuleYamlEditor.QuoteAlways("rendeles_{jobId}.csv"));

        Assert.Contains("  mode: per_job  # append = one growing file, per_job = one file per email", changed);
        Assert.Contains("  file: \"rendeles_{jobId}.csv\"", changed);
        Assert.DoesNotContain("eloreendeles_{yyyy}-{MM}.csv", changed);
        var before = yaml.Split('\n');
        var after = changed.Split('\n');
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(2, before.Zip(after).Count(p => p.First != p.Second)); // only the two lines changed
    }

    [Fact]
    public void SetOutputScalar_inserts_a_missing_key_with_the_block_indentation()
    {
        var changed = RuleYamlEditor.SetOutputScalar(Yaml, "mode", "append");

        Assert.Contains("output:\n  mode: append\n  file: \"x_{yyyy}.csv\"", changed.Replace("\r\n", "\n"));
        Assert.Throws<FormatException>(() => RuleYamlEditor.SetOutputScalar("id: x\n", "mode", "append"));
    }
}

