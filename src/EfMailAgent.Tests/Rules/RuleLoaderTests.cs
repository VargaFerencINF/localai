using EfMailAgent.Rules;

namespace EfMailAgent.Tests.Rules;

public class RuleLoaderTests
{
    [Fact]
    public void Sample_rules_are_valid()
    {
        var set = RuleLoader.LoadAll(TestPaths.SampleRules);

        Assert.Empty(set.InvalidFolders);
        Assert.DoesNotContain(set.Issues, i => !i.IsWarning);
        var rule = Assert.Single(set.Rules);
        Assert.Equal("eloreendeles", rule.Id);
        Assert.Equal("Előrendelés feldolgozás", rule.Name);
        Assert.True(rule.Enabled);
        Assert.Equal(10, rule.Priority);
        Assert.Equal(["előrendelés", "elorendeles", "pre-order"], rule.SubjectContains);
        Assert.Null(rule.SubjectRegex);
        Assert.Equal(["mertekegysegek.md", "partnerek.md"], rule.Skills.Select(s => s.FileName));
        Assert.Equal(11, rule.Output.Columns.Count);
        Assert.Equal(OutputMode.Append, rule.Output.Mode);
        Assert.Equal("eloreendeles_{yyyy}-{MM}.csv", rule.Output.FilePattern);
        Assert.Equal("rows", rule.Output.RowsPath);
        Assert.Equal(20000, rule.Llm.MaxBodyChars);
        Assert.Null(rule.Llm.Model);
        Assert.Equal(0, rule.Llm.Temperature);
        Assert.Contains("előrendelés", rule.PromptText);
        Assert.Contains("{{today}}", set.BaseSystemPrompt);

        var enumColumn = rule.Output.Columns.Single(c => c.Name == "bizonyossag");
        Assert.Equal(ColumnType.Enum, enumColumn.Type);
        Assert.Equal(["magas", "kozepes", "alacsony"], enumColumn.Values);
        Assert.Equal(ColumnType.Date, rule.Output.Columns.Single(c => c.Name == "kert_szallitasi_datum").Type);
    }

    [Fact]
    public void Underscore_and_hidden_folders_are_not_rules()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "jo");
        Directory.CreateDirectory(tmp.Combine("rules", "_base"));
        File.WriteAllText(tmp.Combine("rules", "_base", "system.md"), "rendszer");
        Directory.CreateDirectory(tmp.Combine("rules", "_sablon"));
        Directory.CreateDirectory(tmp.Combine("rules", ".git"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        Assert.Equal(["jo"], set.Rules.Select(r => r.Id));
        Assert.Empty(set.InvalidFolders);
        Assert.Equal("rendszer", set.BaseSystemPrompt);
    }

    [Fact]
    public void Missing_base_prompt_is_a_warning()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "jo");

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        Assert.Single(set.Rules);
        Assert.Contains(set.Issues, i => i.IsWarning && i.Message.Contains("_base/system.md"));
    }

    [Fact]
    public void Missing_prompt_file_is_reported_and_rule_skipped()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "rossz", withPrompt: false);
        RuleFolderBuilder.Create(tmp, "jo");

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        Assert.Equal(["jo"], set.Rules.Select(r => r.Id));
        Assert.Equal(["rossz"], set.InvalidFolders);
        AssertError(set, "rossz", "A prompt fájl nem található: 'prompt.md'");
    }

    [Fact]
    public void Missing_skill_file_is_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "skill", RuleFolderBuilder.ValidYaml.Replace("prompt_file: prompt.md", "prompt_file: prompt.md\nskills:\n  - skills/nincs.md"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "skill", "A skill fájl nem található: 'skills/nincs.md'");
    }

    [Theory]
    [InlineData("../kivul.md")]
    [InlineData("/etc/passwd")]
    public void Prompt_path_outside_rule_folder_is_rejected(string path)
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "kilog", RuleFolderBuilder.ValidYaml.Replace("prompt_file: prompt.md", $"prompt_file: \"{path}\""));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "kilog", "nem mutathat a szabály mappáján kívülre");
    }

    [Fact]
    public void Unknown_column_type_is_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "tipus", RuleFolderBuilder.ValidYaml.Replace("type: integer", "type: decimal"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "tipus", "A(z) 'db' oszlop típusa ismeretlen: 'decimal'");
    }

    [Fact]
    public void Enum_without_values_is_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "enum", RuleFolderBuilder.ValidYaml.Replace("type: integer", "type: enum"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "enum", "A(z) 'db' oszlop enum típusú, de nincs megadva a 'values' lista.");
    }

    [Fact]
    public void Duplicate_ids_keep_the_first_folder()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "a_elso", RuleFolderBuilder.ValidYaml.Replace("id: {ID}", "id: kozos"));
        RuleFolderBuilder.Create(tmp, "b_masodik", RuleFolderBuilder.ValidYaml.Replace("id: {ID}", "id: kozos"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        var rule = Assert.Single(set.Rules);
        Assert.EndsWith("a_elso", rule.FolderPath);
        Assert.Equal(["b_masodik"], set.InvalidFolders);
        AssertError(set, "b_masodik", "Duplikált szabályazonosító: 'kozos'");
    }

    [Fact]
    public void Invalid_regex_is_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "regex", RuleFolderBuilder.ValidYaml.Replace("subject_contains: [\"teszt\"]", "subject_regex: \"^(rma\""));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "regex", "Érvénytelen reguláris kifejezés ('subject_regex')");
    }

    [Fact]
    public void Regex_with_uppercase_letters_gives_warning_but_loads()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "nagy", RuleFolderBuilder.ValidYaml.Replace("subject_contains: [\"teszt\"]", "subject_regex: \"^RMA\\\\d+\""));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        Assert.Single(set.Rules);
        Assert.Contains(set.Issues, i => i.IsWarning && i.Message.Contains("nagybetűs"));
    }

    [Fact]
    public void Escaped_class_letters_in_regex_do_not_warn()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "esc", RuleFolderBuilder.ValidYaml.Replace("subject_contains: [\"teszt\"]", "subject_regex: \"^rma\\\\S+\\\\D\""));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        Assert.Single(set.Rules);
        Assert.DoesNotContain(set.Issues, i => i.Message.Contains("nagybetűs"));
    }

    [Fact]
    public void Yaml_syntax_error_is_reported_with_line()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "szintaxis", "id: szintaxis\nname: [nincs lezárva\n");

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "szintaxis", "Hibás rule.yaml (");
    }

    [Fact]
    public void Unknown_yaml_field_is_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "elgepeles", RuleFolderBuilder.ValidYaml.Replace("priority: 10", "prioritty: 10"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "elgepeles", "ismeretlen mező: 'prioritty'");
    }

    [Fact]
    public void Missing_required_fields_are_all_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "ures", "enabled: true\n");

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "ures", "Hiányzik az 'id' mező.");
        AssertError(set, "ures", "Hiányzik a 'name' mező.");
        AssertError(set, "ures", "Hiányzik a 'match' szakasz.");
        AssertError(set, "ures", "Hiányzik az 'output' szakasz.");
    }

    [Fact]
    public void Folder_without_rule_yaml_is_reported()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("rules", "ures_mappa"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "ures_mappa", "A mappában nincs rule.yaml fájl.");
    }

    [Theory]
    [InlineData("job_id", "foglalt")]
    [InlineData("1rossz", "neve érvénytelen")]
    public void Invalid_column_names_are_reported(string name, string expected)
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "oszlop", RuleFolderBuilder.ValidYaml.Replace("name: db,", $"name: {name},"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "oszlop", expected);
    }

    [Fact]
    public void Duplicate_column_names_are_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "dupla", RuleFolderBuilder.ValidYaml.Replace("name: db,", "name: nev,"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "dupla", "Duplikált oszlopnév: 'nev'.");
    }

    [Theory]
    [InlineData("teszt_{evszam}.csv", "Ismeretlen helyettesítő")]
    [InlineData("almappa/teszt.csv", "nem tartalmazhat mappát")]
    [InlineData("", "Hiányzik az 'output.file'")]
    public void Invalid_output_file_is_reported(string file, string expected)
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "fajl", RuleFolderBuilder.ValidYaml.Replace("file: \"teszt_{yyyy}.csv\"", $"file: \"{file}\""));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "fajl", expected);
    }

    [Fact]
    public void Missing_match_condition_is_reported()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "nincs", RuleFolderBuilder.ValidYaml.Replace("subject_contains: [\"teszt\"]", "subject_contains: []"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        AssertError(set, "nincs", "Nincs illesztési feltétel");
    }

    [Fact]
    public void Disabled_rule_is_loaded_but_never_matches()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "ki", RuleFolderBuilder.ValidYaml.Replace("enabled: true", "enabled: false"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        var rule = Assert.Single(set.Rules);
        Assert.False(rule.Enabled);
        Assert.Null(set.FindBestMatch("teszt"));
    }

    [Fact]
    public void Id_different_from_folder_is_a_warning()
    {
        using var tmp = new TempDir();
        RuleFolderBuilder.Create(tmp, "mappa", RuleFolderBuilder.ValidYaml.Replace("id: {ID}", "id: masik"));

        var set = RuleLoader.LoadAll(tmp.Combine("rules"));

        Assert.Equal("masik", Assert.Single(set.Rules).Id);
        Assert.Contains(set.Issues, i => i.IsWarning && i.Message.Contains("eltér a mappa nevétől"));
    }

    [Fact]
    public void Nonexistent_rules_folder_does_not_throw()
    {
        var set = RuleLoader.LoadAll(Path.Combine(Path.GetTempPath(), "nincs-ilyen-" + Guid.NewGuid()));
        Assert.Empty(set.Rules);
        Assert.NotEmpty(set.Issues);
    }

    [Fact]
    public void Sample_emails_match_their_expected_rule()
    {
        var set = RuleLoader.LoadAll(TestPaths.SampleRules);
        var index = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.SamplesRoot, "emails", "index.json")));

        foreach (var email in index.RootElement.GetProperty("emails").EnumerateArray())
        {
            var file = email.GetProperty("file").GetString()!;
            var subject = email.TryGetProperty("subject", out var s)
                ? s.GetString()
                : File.ReadLines(Path.Combine(TestPaths.SamplesRoot, "emails", file))
                    .First(l => l.StartsWith("Subject:", StringComparison.Ordinal))["Subject:".Length..].Trim();
            var expected = email.GetProperty("expectedRuleId").GetString();

            Assert.True(expected == set.FindBestMatch(subject)?.Id, $"{file}: '{subject}' -> expected {expected ?? "null"}");
        }
    }

    private static void AssertError(RuleSet set, string folder, string messagePart) =>
        Assert.True(set.Issues.Any(i => !i.IsWarning && i.Folder == folder && i.Message.Contains(messagePart)),
            $"Expected error containing \"{messagePart}\" for '{folder}'. Issues:\n" + string.Join("\n", set.Issues.Select(i => $"[{i.Folder}] {i}")));
}
