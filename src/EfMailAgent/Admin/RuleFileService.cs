using System.Text;
using System.Text.RegularExpressions;
using EfMailAgent.Configuration;
using EfMailAgent.Rules;

namespace EfMailAgent.Admin;

public sealed record RuleFileInfo(string Path, string Kind);

public sealed record RuleSummary(
    string Folder,
    string? Id,
    string? Name,
    bool Valid,
    bool Enabled,
    int? Priority,
    IReadOnlyList<string> SubjectContains,
    string? SubjectRegex,
    IReadOnlyList<RuleFileInfo> Files,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool IsBase);

public sealed record SaveResult(bool Saved, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string? Path = null);

/// <summary>Rule folder operations for the admin UI. Every save keeps a .bak of the previous version and reloads the rules.</summary>
public sealed partial class RuleFileService(RuleRepository repository, EffectiveSettings settings)
{
    public const int MaxFileBytes = 1024 * 1024;
    private static readonly string[] EditableExtensions = [".yaml", ".yml", ".md", ".txt"];

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*\\.md$")]
    private static partial Regex SkillNameRegex();

    private readonly object _lock = new();

    public string RulesDir => settings.RulesDir;

    public IReadOnlyList<RuleSummary> List()
    {
        var set = repository.Current;
        var result = new List<RuleSummary>();
        var baseDir = Path.Combine(RulesDir, RuleLoader.BaseFolderName);
        result.Add(new RuleSummary(RuleLoader.BaseFolderName, null, "Közös rendszerprompt", true, true, null, [], null,
            Directory.Exists(baseDir) ? FilesOf(RuleLoader.BaseFolderName) : [new RuleFileInfo(RuleLoader.BaseSystemPromptFile, "base")],
            [], set.Issues.Where(i => i.Folder == RuleLoader.BaseFolderName).Select(i => i.Message).ToArray(), IsBase: true));

        foreach (var folder in RuleFolders())
        {
            var rule = set.Rules.FirstOrDefault(r => string.Equals(Path.GetFileName(r.FolderPath), folder, StringComparison.Ordinal));
            var issues = set.Issues.Where(i => i.Folder == folder).ToArray();
            result.Add(new RuleSummary(folder, rule?.Id, rule?.Name, rule is not null, rule?.Enabled ?? false, rule?.Priority,
                rule?.SubjectContains ?? [], rule?.SubjectRegex, FilesOf(folder),
                issues.Where(i => !i.IsWarning).Select(i => i.Message).ToArray(),
                issues.Where(i => i.IsWarning).Select(i => i.Message).ToArray(), IsBase: false));
        }
        return result;
    }

    public string Read(string folder, string path)
    {
        var full = Resolve(folder, path, mustExist: true);
        return File.ReadAllText(full);
    }

    public SaveResult Save(string folder, string path, string content)
    {
        lock (_lock)
        {
            var full = Resolve(folder, path, mustExist: false);
            if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
                return Fail($"A fájl túl nagy (legfeljebb {MaxFileBytes / 1024} KB).");

            if (IsRuleYaml(folder, path))
            {
                var issues = Validate(folder, content);
                var errors = issues.Where(i => !i.IsWarning).Select(i => i.Message).ToArray();
                if (errors.Length > 0)
                    return new SaveResult(false, errors, issues.Where(i => i.IsWarning).Select(i => i.Message).ToArray());
            }

            WriteWithBackup(full, content);
            return AfterChange(folder, full);
        }
    }

    public SaveResult SetEnabled(string folder, bool enabled)
    {
        lock (_lock)
        {
            var yamlPath = Resolve(folder, RuleLoader.RuleFileName, mustExist: true);
            var yaml = RuleYamlEditor.SetTopLevel(File.ReadAllText(yamlPath), "enabled", enabled ? "true" : "false");
            return SaveYamlChecked(folder, yamlPath, yaml);
        }
    }

    public SaveResult AddSkill(string folder, string fileName, string content)
    {
        lock (_lock)
        {
            if (!SkillNameRegex().IsMatch(fileName))
                return Fail("A skill fájl neve csak kisbetűt, számot, '-' és '_' jelet tartalmazhat, és .md végű legyen (pl. cikklista.md).");
            var relative = $"skills/{fileName}";
            var full = Resolve(folder, relative, mustExist: false);
            if (File.Exists(full))
                return Fail($"Már létezik: {relative}");

            var yamlPath = Resolve(folder, RuleLoader.RuleFileName, mustExist: true);
            var yamlText = File.ReadAllText(yamlPath);
            var skills = RuleYamlEditor.GetSkills(yamlText).Append(relative).ToArray();
            var newYaml = RuleYamlEditor.SetSkills(yamlText, skills);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            var result = SaveYamlChecked(folder, yamlPath, newYaml);
            if (!result.Saved)
                File.Delete(full);
            return result with { Path = relative };
        }
    }

    public SaveResult RemoveSkill(string folder, string relative)
    {
        lock (_lock)
        {
            var full = Resolve(folder, relative, mustExist: true);
            var yamlPath = Resolve(folder, RuleLoader.RuleFileName, mustExist: true);
            if (IsRuleYaml(folder, relative) || string.Equals(full, Resolve(folder, PromptFileOf(folder), mustExist: false), StringComparison.Ordinal))
                return Fail("A rule.yaml és a prompt fájl nem törölhető.");

            var yamlText = File.ReadAllText(yamlPath);
            var skills = RuleYamlEditor.GetSkills(yamlText)
                .Where(s => !string.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(yamlPath)!, s)), full, StringComparison.Ordinal))
                .ToArray();
            var newYaml = RuleYamlEditor.SetSkills(yamlText, skills);

            File.Move(full, full + ".bak", overwrite: true); // keep the content recoverable
            var result = SaveYamlChecked(folder, yamlPath, newYaml);
            if (!result.Saved)
                File.Move(full + ".bak", full, overwrite: true);
            return result;
        }
    }

    public SaveResult Create(string id, string name, string? copyFrom)
    {
        lock (_lock)
        {
            id = id.Trim();
            name = name.Trim();
            if (!IdRegex().IsMatch(id))
                return Fail("Az azonosító csak kisbetűt, számot, '-' és '_' jelet tartalmazhat, és betűvel vagy számmal kell kezdődnie.");
            if (name.Length == 0)
                return Fail("A név megadása kötelező.");
            var target = Path.Combine(RulesDir, id);
            if (Directory.Exists(target) || repository.Current.FindById(id) is not null)
                return Fail($"Már létezik ilyen szabály vagy mappa: {id}");

            if (copyFrom is not null)
            {
                var source = Path.Combine(RulesDir, ValidFolder(copyFrom));
                var sourceRule = repository.Current.Rules.FirstOrDefault(r => Path.GetFileName(r.FolderPath) == copyFrom);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => RulesVersion.IsRelevant(source, f)))
                {
                    var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
                var yamlPath = Path.Combine(target, RuleLoader.RuleFileName);
                var yaml = File.ReadAllText(yamlPath);
                yaml = RuleYamlEditor.SetTopLevel(yaml, "id", id);
                yaml = RuleYamlEditor.SetTopLevel(yaml, "name", RuleYamlEditor.QuoteAlways(name));
                yaml = RuleYamlEditor.SetTopLevel(yaml, "enabled", "false");
                if (sourceRule is not null)
                    yaml = RuleYamlEditor.ReplaceInOutputFile(yaml, sourceRule.Id, id);
                File.WriteAllText(yamlPath, yaml);
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(target, "skills"));
                File.WriteAllText(Path.Combine(target, RuleLoader.RuleFileName), RuleTemplates.RuleYaml(id, name));
                File.WriteAllText(Path.Combine(target, RuleLoader.DefaultPromptFile), RuleTemplates.Prompt(name));
            }

            var result = AfterChange(id, Path.Combine(target, RuleLoader.RuleFileName));
            return result with { Path = id };
        }
    }

    /// <summary>Validates a rule.yaml text as if it were saved: a temporary copy of the folder is loaded by the real loader.</summary>
    public IReadOnlyList<RuleIssue> Validate(string folder, string yaml)
    {
        var source = Path.Combine(RulesDir, folder);
        var temp = Path.Combine(Path.GetTempPath(), "efmailagent-validate-" + Guid.NewGuid().ToString("N"), folder);
        try
        {
            Directory.CreateDirectory(temp);
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(temp, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }
            File.WriteAllText(Path.Combine(temp, RuleLoader.RuleFileName), yaml);

            var issues = new List<RuleIssue>();
            var rule = RuleLoader.LoadRule(temp, folder, issues);
            if (rule is not null)
            {
                var clash = repository.Current.Rules.FirstOrDefault(r => r.Id == rule.Id && Path.GetFileName(r.FolderPath) != folder);
                if (clash is not null)
                    issues.Add(new RuleIssue(folder, $"Duplikált szabályazonosító: '{rule.Id}' (már használja a(z) '{Path.GetFileName(clash.FolderPath)}' mappa)."));
            }
            return issues;
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private SaveResult SaveYamlChecked(string folder, string yamlPath, string yaml)
    {
        var issues = Validate(folder, yaml);
        var errors = issues.Where(i => !i.IsWarning).Select(i => i.Message).ToArray();
        if (errors.Length > 0)
            return new SaveResult(false, errors, issues.Where(i => i.IsWarning).Select(i => i.Message).ToArray());
        WriteWithBackup(yamlPath, yaml);
        return AfterChange(folder, yamlPath);
    }

    private SaveResult AfterChange(string folder, string fullPath)
    {
        var set = repository.Reload();
        var issues = set.Issues.Where(i => i.Folder == folder).ToArray();
        return new SaveResult(true, issues.Where(i => !i.IsWarning).Select(i => i.Message).ToArray(),
            issues.Where(i => i.IsWarning).Select(i => i.Message).ToArray(), Path.GetRelativePath(RulesDir, fullPath).Replace('\\', '/'));
    }

    private static void WriteWithBackup(string full, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full))
            File.Copy(full, full + ".bak", overwrite: true);
        var temp = full + ".tmp";
        File.WriteAllText(temp, content.Replace("\r\n", "\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, full, overwrite: true);
    }

    private IReadOnlyList<RuleFileInfo> FilesOf(string folder)
    {
        var dir = Path.Combine(RulesDir, folder);
        if (!Directory.Exists(dir))
            return [];
        var promptFile = PromptFileOf(folder);
        var skills = File.Exists(Path.Combine(dir, RuleLoader.RuleFileName))
            ? SafeSkills(File.ReadAllText(Path.Combine(dir, RuleLoader.RuleFileName)))
            : [];
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => RulesVersion.IsRelevant(dir, f) && EditableExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .OrderBy(f => f == RuleLoader.RuleFileName ? 0 : f == promptFile ? 1 : skills.Contains(f) ? 2 : 3)
            .ThenBy(f => f, StringComparer.Ordinal)
            .Select(f => new RuleFileInfo(f, folder == RuleLoader.BaseFolderName ? "base"
                : f == RuleLoader.RuleFileName ? "rule" : f == promptFile ? "prompt" : skills.Contains(f) ? "skill" : "other"))
            .ToArray();
    }

    private static IReadOnlyList<string> SafeSkills(string yaml)
    {
        try
        {
            return RuleYamlEditor.GetSkills(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return [];
        }
    }

    private string PromptFileOf(string folder)
    {
        var rule = repository.Current.Rules.FirstOrDefault(r => Path.GetFileName(r.FolderPath) == folder);
        return rule?.PromptFile ?? RuleLoader.DefaultPromptFile;
    }

    private IEnumerable<string> RuleFolders() =>
        Directory.Exists(RulesDir)
            ? Directory.EnumerateDirectories(RulesDir).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith('_') && !n.StartsWith('.')).OrderBy(n => n, StringComparer.Ordinal)
            : [];

    private static bool IsRuleYaml(string folder, string path) =>
        folder != RuleLoader.BaseFolderName && path.Replace('\\', '/') == RuleLoader.RuleFileName;

    private string ValidFolder(string folder)
    {
        if (folder != RuleLoader.BaseFolderName && !RuleFolders().Contains(folder))
            throw new AdminRequestException($"Ismeretlen szabálymappa: {folder}", 404);
        return folder;
    }

    private string Resolve(string folder, string path, bool mustExist)
    {
        ValidFolder(folder);
        var root = Path.GetFullPath(Path.Combine(RulesDir, folder));
        var full = RuleLoader.ResolveInside(root, path.Replace('\\', '/'))
            ?? throw new AdminRequestException("A fájl útvonala nem mutathat a szabály mappáján kívülre.", 400);
        if (!EditableExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
            throw new AdminRequestException("Csak .yaml, .md és .txt fájl szerkeszthető.", 400);
        if (Path.GetFileName(full).StartsWith('.'))
            throw new AdminRequestException("Rejtett fájl nem szerkeszthető.", 400);
        if (mustExist && !File.Exists(full))
            throw new AdminRequestException($"A fájl nem található: {path}", 404);
        return full;
    }

    private static SaveResult Fail(string message) => new(false, [message], []);
}

public sealed class AdminRequestException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class RuleTemplates
{
    public static string RuleYaml(string id, string name) => $$"""
        id: {{id}}
        name: {{RuleYamlEditor.QuoteAlways(name)}}
        enabled: false               # kapcsold be, ha a prompt elkészült és a Tesztkonzolban jól működik
        priority: 100                # kisebb szám = erősebb, ha több szabály is illeszkedik
        match:
          subject_contains: ["{{id}}"]   # ezek a szavak a tárgyban (ékezettől és kis/nagybetűtől függetlenül)
          subject_regex: null
        prompt_file: prompt.md
        skills: []
        output:
          mode: append               # append = egy folyamatosan bővülő fájl, per_job = levelenként külön fájl
          file: "{{id}}_{yyyy}-{MM}.csv"
          rows_path: rows
          columns:
            - { name: megnevezes,  type: string, required: true,  description: "A tétel megnevezése, ahogy az e-mailben szerepel" }
            - { name: mennyiseg,   type: number, required: false, description: "Mennyiség számként, ha szerepel" }
            - { name: megjegyzes,  type: string, required: false, description: "Minden egyéb releváns információ" }
            - { name: bizonyossag, type: enum, values: [magas, kozepes, alacsony], required: true, description: "Mennyire biztos a sor helyessége" }
        llm:
          model: null
          temperature: 0
          max_body_chars: 20000

        """;

    public static string Prompt(string name) => $"""
        ## Feladat: {name}

        Írd le itt, milyen e-maileket dolgoz fel ez a szabály, és mit kell kinyerni belőlük.

        Szabályok:
        - Minden tétel külön sor a "rows" tömbben.
        - Csak azt írd be, ami egyértelműen szerepel az e-mailben; ami hiányzik, az legyen null.
        - Ha valamit értelmezni kellett, a bizonyossag "kozepes", ha bizonytalan vagy feltételes, "alacsony".

        """;
}
