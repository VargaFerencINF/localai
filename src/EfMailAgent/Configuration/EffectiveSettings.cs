namespace EfMailAgent.Configuration;

/// <summary>Settings plus absolute paths, as used by the rest of the application.</summary>
public sealed class EffectiveSettings(DataDirectory dataDirectory, AppSettings settings)
{
    public DataDirectory DataDirectory { get; } = dataDirectory;
    public AppSettings Settings { get; } = settings;

    public string SettingsFile => Path.Combine(DataDirectory.Path, SettingsBootstrapper.SettingsFileName);
    public string RulesDir => Resolve(Settings.Paths.Rules);
    public string OutputDir => Resolve(Settings.Paths.Output);
    public string DataDir => Resolve(Settings.Paths.Data);
    public string LogsDir => Resolve(Settings.Paths.Logs);
    public string AddonDir => Path.Combine(DataDirectory.Path, "addon");

    private string Resolve(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(DataDirectory.Path, path));
}
