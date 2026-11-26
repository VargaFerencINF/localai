using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfMailAgent.Rules;

/// <summary>
/// Holds the current <see cref="RuleSet"/> and hot-reloads it when files in the rules folder change
/// (FileSystemWatcher + debounce). Reloading never throws; invalid rules are logged and skipped.
/// </summary>
public sealed class RuleRepository : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

    private readonly string _rulesDir;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly object _reloadLock = new();
    private readonly Timer _debounceTimer;
    private FileSystemWatcher? _watcher;
    private RuleSet _current;
    private bool _disposed;

    public RuleRepository(string rulesDir, ILogger<RuleRepository>? logger = null, TimeSpan? debounce = null)
    {
        _rulesDir = Path.GetFullPath(rulesDir);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _debounce = debounce ?? DefaultDebounce;
        _current = RuleSet.Empty(_rulesDir);
        _debounceTimer = new Timer(_ => ReloadFromWatcher(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public RuleSet Current => Volatile.Read(ref _current);

    public string RulesDir => _rulesDir;

    /// <summary>Raised after a reload that produced a new <see cref="RuleSet.Version"/>.</summary>
    public event EventHandler<RuleSet>? Reloaded;

    /// <summary>Initial load plus file watching.</summary>
    public RuleSet Start()
    {
        Directory.CreateDirectory(_rulesDir);
        var set = Reload(force: true);
        StartWatching();
        return set;
    }

    /// <summary>Loads the rules now. Returns the current set (unchanged if the version did not change).</summary>
    public RuleSet Reload(bool force = false)
    {
        lock (_reloadLock)
        {
            RuleSet set;
            try
            {
                set = RuleLoader.LoadAll(_rulesDir);
            }
            catch (Exception ex)
            {
                // Defensive: the loader reports content errors itself, this only guards unexpected I/O failures.
                _logger.LogError(ex, "A szabályok betöltése váratlan hibával leállt; az előző szabálykészlet marad érvényben.");
                return Current;
            }

            var previous = Current;
            if (!force && set.Version == previous.Version)
                return previous;

            Volatile.Write(ref _current, set);
            LogResult(set, previous);
            try
            {
                Reloaded?.Invoke(this, set);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hiba a szabályok újratöltését követő értesítés közben.");
            }
            return set;
        }
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(_rulesDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "A szabálymappa figyelése hibát jelzett; teljes újratöltés következik.");
            ScheduleReload();
        };
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Backups and temp files (e.g. written by an editor or the admin UI) do not trigger a reload.
        if (e is not RenamedEventArgs && !RulesVersion.IsRelevant(_rulesDir, e.FullPath))
            return;
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        if (!_disposed)
            _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void ReloadFromWatcher()
    {
        if (!_disposed)
            Reload();
    }

    private void LogResult(RuleSet set, RuleSet previous)
    {
        var verb = previous.Rules.Count == 0 && previous.Issues.Count == 0 ? "betöltve" : "újratöltve";
        _logger.LogInformation(
            "Szabályok {Verb}: {RuleCount} érvényes ({EnabledCount} bekapcsolva), {InvalidCount} hibás, verzió {RulesVersion}",
            verb, set.Rules.Count, set.EnabledRules.Count(), set.InvalidFolders.Count, set.Version);

        foreach (var issue in set.Issues)
        {
            if (issue.IsWarning)
                _logger.LogWarning("Szabály figyelmeztetés ({Folder}): {Message}", issue.Folder, issue.Message);
            else
                _logger.LogError("Szabályhiba ({Folder}): {Message}", issue.Folder, issue.Message);
        }

        foreach (var folder in set.InvalidFolders)
            _logger.LogError("A(z) '{Folder}' szabály hibás, ezért kimarad a feldolgozásból.", folder);
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _debounceTimer.Dispose();
    }
}
