namespace EfMailAgent.Configuration;

public enum DataDirectorySource
{
    CommandLine,
    EnvironmentVariable,
    ProductionDefault,
    DevelopmentDefault,
}

public sealed record DataDirectory(string Path, DataDirectorySource Source)
{
    public const string EnvironmentVariableName = "EFMAILAGENT_DATA_DIR";
    public const string DevFolderName = ".devdata";

    public static string ProductionDefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EfMailAgent");

    /// <summary>True when the folder is not the installed production location, i.e. dev/test/CI data.</summary>
    public bool IsDevelopment => Source != DataDirectorySource.ProductionDefault
        && !string.Equals(System.IO.Path.GetFullPath(Path).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            System.IO.Path.GetFullPath(ProductionDefaultPath).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolution order: --data-dir, EFMAILAGENT_DATA_DIR, then C:\ProgramData\EfMailAgent for a Release build on
    /// Windows, otherwise ./.devdata (Debug builds and non-Windows machines are development machines).
    /// </summary>
    public static DataDirectory Resolve(string? commandLineValue, string? environmentValue = null, bool? productionBuild = null)
    {
        environmentValue ??= Environment.GetEnvironmentVariable(EnvironmentVariableName);

        if (!string.IsNullOrWhiteSpace(commandLineValue))
            return new DataDirectory(System.IO.Path.GetFullPath(commandLineValue), DataDirectorySource.CommandLine);

        if (!string.IsNullOrWhiteSpace(environmentValue))
            return new DataDirectory(System.IO.Path.GetFullPath(environmentValue), DataDirectorySource.EnvironmentVariable);

        if (productionBuild ?? IsProductionBuild())
            return new DataDirectory(ProductionDefaultPath, DataDirectorySource.ProductionDefault);

        return new DataDirectory(System.IO.Path.GetFullPath(DevFolderName), DataDirectorySource.DevelopmentDefault);
    }

    private static bool IsProductionBuild()
    {
#if DEBUG
        return false;
#else
        return OperatingSystem.IsWindows();
#endif
    }
}
