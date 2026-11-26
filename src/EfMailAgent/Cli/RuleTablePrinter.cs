using EfMailAgent.Configuration;
using EfMailAgent.Rules;

namespace EfMailAgent.Cli;

internal static class RuleTablePrinter
{
    public static void Print(RuleSet set, EffectiveSettings settings, TextWriter output)
    {
        output.WriteLine($"Adatmappa:          {settings.DataDirectory.Path}");
        output.WriteLine($"Szabálymappa:       {set.RulesDir}");
        output.WriteLine($"Szabályok verziója: {set.Version}");
        output.WriteLine();

        var rows = new List<string[]> { new[] { "AZONOSÍTÓ", "NÉV", "ÁLLAPOT", "PRIORITÁS", "ILLESZTÉS" } };
        foreach (var rule in set.Rules.OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            var match = string.Join(", ", rule.SubjectContains);
            if (rule.SubjectRegex is not null)
                match += (match.Length > 0 ? " | " : "") + "regex: " + rule.SubjectRegex;
            rows.Add([rule.Id, rule.Name, rule.Enabled ? "érvényes" : "érvényes, kikapcsolva", rule.Priority.ToString(), match]);
        }
        foreach (var folder in set.InvalidFolders)
            rows.Add([folder, "-", "HIBÁS", "-", "-"]);

        if (rows.Count == 1)
        {
            output.WriteLine("Nincs egyetlen szabály sem a szabálymappában.");
        }
        else
        {
            var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
            foreach (var (row, index) in rows.Select((r, i) => (r, i)))
            {
                output.WriteLine(string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))).TrimEnd());
                if (index == 0)
                    output.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
            }
        }

        if (set.Issues.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Hibák és figyelmeztetések:");
            foreach (var issue in set.Issues)
                output.WriteLine($"  [{(issue.Folder.Length > 0 ? issue.Folder : "-")}] {(issue.IsWarning ? "figyelmeztetés: " : "HIBA: ")}{issue.Message}");
        }

        output.WriteLine();
        output.WriteLine($"Összesen: {set.Rules.Count} érvényes, {set.InvalidFolders.Count} hibás szabály.");
    }
}
