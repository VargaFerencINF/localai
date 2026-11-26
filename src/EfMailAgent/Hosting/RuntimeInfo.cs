namespace EfMailAgent.Hosting;

/// <summary>How this process runs: as the Windows service, or started by a person (desktop app / console).</summary>
public sealed class RuntimeInfo
{
    public bool IsWindowsService { get; init; }

    /// <summary>The UI may stop the program only when it is not the service (the service is stopped in Windows).</summary>
    public bool QuitAllowed => !IsWindowsService;
}
