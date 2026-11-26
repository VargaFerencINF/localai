using System.Text.RegularExpressions;
using EfMailAgent.Matching;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EfMailAgent.Rules;

/// <summary>
/// Loads and validates every rule folder. Invalid rules are reported (Hungarian messages) and skipped; nothing here
/// throws for bad rule content.
/// </summary>
public static partial class RuleLoader
{
    public const string RuleFileName = "rule.yaml";
    public const string BaseFolderName = "_base";
    public const string BaseSystemPromptFile = "system.md";
    public const string DefaultPromptFile = "prompt.md";
    public const string DefaultRowsPath = "rows";
    public const int DefaultPriority = 100;

    /// <summary>Columns the CSV writer prepends to every row; rule columns must not reuse these names.</summary>
    public static readonly IReadOnlyList<string> MetaColumns =
        ["job_id", "feldolgozva", "email_datum", "kuldo", "eredeti_kuldo", "targy", "message_id", "statusz"];

    public static readonly IReadOnlyList<string> FileNameTokens = ["yyyy", "MM", "dd", "ruleId", "jobId"];

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("\\{([^{}]*)\\}")]
    private static partial Regex FileTokenRegex();

    [GeneratedRegex("\\\\.")]
    private static partial Regex RegexEscapeRegex();

    public static RuleSet LoadAll(string rulesDir)
    {
        // Retry when files change while we read them, so Version always describes the loaded content.
        for (var attempt = 0; ; attempt++)
        {
            var before = RulesVersion.Compute(rulesDir);
            var set = LoadOnce(rulesDir, before);
            if (attempt >= 2 || RulesVersion.Compute(rulesDir) == before)
                return set;
        }
    }

    private static RuleSet LoadOnce(string rulesDir, string version)
    {
        var issues = new List<RuleIssue>();
        var rules = new List<Rule>();
        var invalidFolders = new List<string>();

        if (!Directory.Exists(rulesDir))
        {
            issues.Add(new RuleIssue("", $"A szabályok mappája nem létezik: {rulesDir}"));
            return new RuleSet(rulesDir, version, rules, issues, invalidFolders, "", DateTimeOffset.UtcNow);
        }

        var baseSystemPrompt = "";
        var basePath = Path.Combine(rulesDir, BaseFolderName, BaseSystemPromptFile);
        if (File.Exists(basePath))
            baseSystemPrompt = TryRead(basePath, BaseFolderName, issues) ?? "";
        else
            issues.Add(new RuleIssue(BaseFolderName, $"Hiányzik a közös rendszerprompt: {BaseFolderName}/{BaseSystemPromptFile}", IsWarning: true));

        var folders = Directory.EnumerateDirectories(rulesDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.StartsWith('_') && !n.StartsWith('.'))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var folderIssues = new List<RuleIssue>();
            var rule = LoadRule(Path.Combine(rulesDir, folder), folder, folderIssues);
            if (rule is not null)
            {
                var existing = rules.FirstOrDefault(r => r.Id == rule.Id);
                if (existing is not null)
                {
                    folderIssues.Add(new RuleIssue(folder,
                        $"Duplikált szabályazonosító: '{rule.Id}' (már használja a(z) '{Path.GetFileName(existing.FolderPath)}' mappa). A szabály kihagyva."));
                    rule = null;
                }
            }

            issues.AddRange(folderIssues);
            if (rule is not null)
                rules.Add(rule);
            else
                invalidFolders.Add(folder);
        }

        return new RuleSet(rulesDir, version, rules, issues, invalidFolders, baseSystemPrompt, DateTimeOffset.UtcNow);
    }

    /// <summary>Loads one rule folder. Returns null (with at least one error in <paramref name="issues"/>) when invalid.</summary>
    public static Rule? LoadRule(string folderPath, string folderName, List<RuleIssue> issues)
    {
        var errorCountBefore = issues.Count(i => !i.IsWarning);
        void Error(string message) => issues.Add(new RuleIssue(folderName, message));
        void Warn(string message) => issues.Add(new RuleIssue(folderName, message, IsWarning: true));
        bool HasNewErrors() => issues.Count(i => !i.IsWarning) > errorCountBefore;

        var rulePath = Path.Combine(folderPath, RuleFileName);
        if (!File.Exists(rulePath))
        {
            Error($"A mappában nincs {RuleFileName} fájl.");
            return null;
        }

        var yaml = TryRead(rulePath, folderName, issues);
        if (yaml is null)
            return null;

        RuleDocument? doc;
        try
        {
            doc = Deserializer.Deserialize<RuleDocument>(yaml);
        }
        catch (YamlException ex)
        {
            Error($"Hibás {RuleFileName} ({ex.Start.Line}. sor, {ex.Start.Column}. oszlop): {DescribeYamlError(ex)}");
            return null;
        }

        if (doc is null)
        {
            Error($"A {RuleFileName} üres.");
            return null;
        }

        // id, name
        var id = doc.Id?.Trim() ?? "";
        if (id.Length == 0)
            Error("Hiányzik az 'id' mező.");
        else if (!IdRegex().IsMatch(id))
            Error($"Érvénytelen 'id': '{id}'. Csak kisbetű, szám, '-' és '_' használható, és betűvel vagy számmal kell kezdődnie.");
        else if (id != folderName)
            Warn($"Az 'id' ('{id}') eltér a mappa nevétől ('{folderName}').");

        var name = doc.Name?.Trim() ?? "";
        if (name.Length == 0)
            Error("Hiányzik a 'name' mező.");

        var priority = doc.Priority ?? DefaultPriority;

        // match
        var keywords = new List<string>();
        string? regex = null;
        if (doc.Match is null)
        {
            Error("Hiányzik a 'match' szakasz.");
        }
        else
        {
            foreach (var keyword in doc.Match.SubjectContains ?? [])
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    Warn("A 'subject_contains' üres elemet tartalmaz, ez figyelmen kívül marad.");
                else if (SubjectNormalizer.NormalizeText(keyword).Length == 0)
                    Warn($"A '{keyword}' kulcsszó normalizálás után üres, ez figyelmen kívül marad.");
                else
                    keywords.Add(keyword);
            }

            if (!string.IsNullOrWhiteSpace(doc.Match.SubjectRegex))
            {
                regex = doc.Match.SubjectRegex;
                try
                {
                    SubjectMatcher.CreateRegex(regex);
                    var withoutEscapes = RegexEscapeRegex().Replace(regex, "");
                    if (withoutEscapes.Any(c => char.IsUpper(c) || (char.IsLetter(c) && c > 127)))
                        Warn("A 'subject_regex' a normalizált (kisbetűs, ékezet nélküli) tárgyra fut; a benne lévő nagybetűs vagy ékezetes betűk sosem fognak illeszkedni.");
                }
                catch (ArgumentException ex)
                {
                    Error($"Érvénytelen reguláris kifejezés ('subject_regex'): {ex.Message}");
                }
            }

            if (keywords.Count == 0 && regex is null)
                Error("Nincs illesztési feltétel: adj meg legalább egy 'subject_contains' kulcsszót vagy egy 'subject_regex' kifejezést.");
        }

        // prompt + skills
        var promptFile = string.IsNullOrWhiteSpace(doc.PromptFile) ? DefaultPromptFile : doc.PromptFile.Trim();
        string? promptText = null;
        var promptPath = ResolveInside(folderPath, promptFile);
        if (promptPath is null)
            Error($"A 'prompt_file' nem mutathat a szabály mappáján kívülre: '{promptFile}'.");
        else if (!File.Exists(promptPath))
            Error($"A prompt fájl nem található: '{promptFile}'.");
        else
            promptText = TryRead(promptPath, folderName, issues);

        var skills = new List<RuleSkill>();
        foreach (var skill in doc.Skills ?? [])
        {
            if (string.IsNullOrWhiteSpace(skill))
            {
                Warn("A 'skills' lista üres elemet tartalmaz, ez figyelmen kívül marad.");
                continue;
            }

            var skillPath = ResolveInside(folderPath, skill.Trim());
            if (skillPath is null)
                Error($"A skill fájl nem mutathat a szabály mappáján kívülre: '{skill}'.");
            else if (!File.Exists(skillPath))
                Error($"A skill fájl nem található: '{skill}'.");
            else if (TryRead(skillPath, folderName, issues) is { } content)
                skills.Add(new RuleSkill(skill.Trim(), Path.GetFileName(skillPath), content));
        }

        // output
        var output = ValidateOutput(doc.Output, Error, Warn);

        // llm overrides
        var llm = doc.Llm;
        if (llm?.Temperature is < 0 or > 2)
            Error($"Érvénytelen 'llm.temperature': {llm.Temperature} (0 és 2 közötti szám kell).");
        if (llm?.MaxBodyChars is < 100 or > 1_000_000)
            Error($"Érvénytelen 'llm.max_body_chars': {llm.MaxBodyChars} (100 és 1000000 közötti szám kell).");
        var overrides = new RuleLlmOverrides(
            string.IsNullOrWhiteSpace(llm?.Model) ? null : llm.Model.Trim(),
            llm?.Temperature,
            llm?.MaxBodyChars ?? Rule.DefaultMaxBodyChars);

        if (HasNewErrors() || output is null || promptText is null)
            return null;

        var enabled = doc.Enabled ?? true;
        return new Rule(id, name, enabled, priority, keywords, regex, Path.GetFullPath(folderPath), promptFile, promptText,
            skills, output, overrides)
        {
            Matcher = new SubjectMatcher(id, priority, enabled, keywords, regex),
        };
    }

    private static RuleOutput? ValidateOutput(OutputDocument? output, Action<string> error, Action<string> warn)
    {
        if (output is null)
        {
            error("Hiányzik az 'output' szakasz.");
            return null;
        }

        var ok = true;
        var mode = OutputMode.Append;
        switch (output.Mode?.Trim())
        {
            case null or "" or "append":
                break;
            case "per_job":
                mode = OutputMode.PerJob;
                break;
            default:
                error($"Ismeretlen 'output.mode': '{output.Mode}' (megengedett: append, per_job).");
                ok = false;
                break;
        }

        var file = output.File?.Trim() ?? "";
        if (file.Length == 0)
        {
            error("Hiányzik az 'output.file' (a CSV fájl neve).");
            ok = false;
        }
        else
        {
            foreach (Match m in FileTokenRegex().Matches(file))
            {
                if (!FileNameTokens.Contains(m.Groups[1].Value))
                {
                    error($"Ismeretlen helyettesítő az 'output.file' értékében: '{m.Value}' (megengedett: {string.Join(", ", FileNameTokens.Select(t => "{" + t + "}"))}).");
                    ok = false;
                }
            }

            var literal = FileTokenRegex().Replace(file, "x");
            if (literal.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || literal.Any(char.IsControl)
                || literal.Contains('{') || literal.Contains('}'))
            {
                error($"Az 'output.file' nem tartalmazhat mappát vagy tiltott karaktert: '{file}'.");
                ok = false;
            }
            else if (!file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                warn($"Az 'output.file' nem .csv kiterjesztésű: '{file}'.");
            }

            if (mode == OutputMode.PerJob && !file.Contains("{jobId}"))
                warn("per_job módban az 'output.file' értékében érdemes a {jobId} helyettesítőt használni, különben a fájlok sorszámozott utótagot kapnak.");
        }

        var rowsPath = string.IsNullOrWhiteSpace(output.RowsPath) ? DefaultRowsPath : output.RowsPath.Trim();
        if (!IdentifierRegex().IsMatch(rowsPath))
        {
            error($"Érvénytelen 'output.rows_path': '{rowsPath}' (betűvel vagy '_' jellel kezdődő azonosító kell).");
            ok = false;
        }

        var columns = new List<RuleColumn>();
        if (output.Columns is null || output.Columns.Count == 0)
        {
            error("Az 'output.columns' lista üres: legalább egy oszlopot meg kell adni.");
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < output.Columns.Count; i++)
        {
            var c = output.Columns[i];
            var position = $"{i + 1}. oszlop";
            var name = c?.Name?.Trim() ?? "";
            if (c is null || name.Length == 0)
            {
                error($"A(z) {position} nevének ('name') megadása kötelező.");
                ok = false;
                continue;
            }

            var label = $"'{name}' oszlop";
            if (!IdentifierRegex().IsMatch(name))
            {
                error($"A(z) {label} neve érvénytelen: csak betű, szám és '_' használható, és nem kezdődhet számmal.");
                ok = false;
            }
            else if (MetaColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                error($"A(z) {label} neve foglalt, mert a CSV automatikusan hozzáadja ({string.Join(", ", MetaColumns)}).");
                ok = false;
            }
            else if (!seen.Add(name))
            {
                error($"Duplikált oszlopnév: '{name}'.");
                ok = false;
            }

            ColumnType? type = c.Type?.Trim() switch
            {
                "string" => ColumnType.String,
                "number" => ColumnType.Number,
                "integer" => ColumnType.Integer,
                "date" => ColumnType.Date,
                "boolean" => ColumnType.Boolean,
                "enum" => ColumnType.Enum,
                _ => null,
            };
            if (type is null)
            {
                error(string.IsNullOrWhiteSpace(c.Type)
                    ? $"A(z) {label} típusa ('type') hiányzik."
                    : $"A(z) {label} típusa ismeretlen: '{c.Type}' (megengedett: string, number, integer, date, boolean, enum).");
                ok = false;
                continue;
            }

            var values = (c.Values ?? []).Select(v => v?.Trim() ?? "").ToList();
            if (type == ColumnType.Enum)
            {
                if (values.Count == 0)
                {
                    error($"A(z) {label} enum típusú, de nincs megadva a 'values' lista.");
                    ok = false;
                }
                else if (values.Any(v => v.Length == 0))
                {
                    error($"A(z) {label} 'values' listája üres értéket tartalmaz.");
                    ok = false;
                }
                else if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
                {
                    error($"A(z) {label} 'values' listájában ismétlődő érték van.");
                    ok = false;
                }
            }
            else if (values.Count > 0)
            {
                warn($"A(z) {label} nem enum típusú, ezért a 'values' lista figyelmen kívül marad.");
                values = [];
            }

            if (string.IsNullOrWhiteSpace(c.Description))
                warn($"A(z) {label} leírása ('description') hiányzik; a modell ebből érti meg, mit kell kinyernie.");

            columns.Add(new RuleColumn(name, type.Value, c.Required ?? false, c.Description?.Trim() ?? "", values));
        }

        return ok ? new RuleOutput(mode, file, rowsPath, columns) : null;
    }

    /// <summary>Resolves a path relative to the rule folder; null if it would escape the folder.</summary>
    internal static string? ResolveInside(string folderPath, string relative)
    {
        if (Path.IsPathRooted(relative))
            return null;
        var root = Path.GetFullPath(folderPath);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(rootWithSeparator, comparison) ? full : null;
    }

    private static string? TryRead(string path, string folder, List<RuleIssue> issues)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new RuleIssue(folder, $"A fájl nem olvasható: '{Path.GetFileName(path)}' ({ex.Message})."));
            return null;
        }
    }

    private static string DescribeYamlError(YamlException ex)
    {
        var inner = ex.InnerException?.Message;
        var message = ex.Message;
        if (message.Contains("not found on type", StringComparison.Ordinal) || message.Contains("Property '", StringComparison.Ordinal))
        {
            var match = Regex.Match(message, "Property '([^']+)'");
            if (match.Success)
                return $"ismeretlen mező: '{match.Groups[1].Value}'.";
        }
        if (inner is not null && inner.Contains("was not recognized as a valid", StringComparison.Ordinal))
            return $"nem megfelelő típusú érték ({inner}).";
        return inner is null ? message : $"{message} ({inner})";
    }
}
