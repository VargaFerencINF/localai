using System.Reflection;

namespace EfMailAgent;

public static class VersionInfo
{
    /// <summary>Product version from the VERSION file (via Directory.Build.props).</summary>
    public static string Current { get; } =
        typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";
}
