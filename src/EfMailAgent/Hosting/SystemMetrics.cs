using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EfMailAgent.Hosting;

public sealed record GpuInfo(string Name, double? DedicatedVramGb, bool Discrete);

public sealed record HardwareInfo(double TotalRamGb, string? CpuName, int LogicalCores, IReadOnlyList<GpuInfo> Gpus, double? FreeDiskGb, string Os)
{
    /// <summary>The discrete graphics card with the most memory, if any (Ollama can offload to it).</summary>
    public GpuInfo? BestDiscreteGpu => Gpus.Where(g => g.Discrete).OrderByDescending(g => g.DedicatedVramGb ?? 0).FirstOrDefault();
}

/// <summary>Machine facts and live load for the model advisor and the Teljesítmény page. Values that cannot be measured are null.</summary>
public interface ISystemMetrics
{
    HardwareInfo Hardware();

    /// <summary>Total and available physical memory in GB.</summary>
    (double TotalGb, double AvailableGb)? Memory();

    /// <summary>Processor load in percent since the previous call (null on the first call).</summary>
    double? CpuPercent();

    /// <summary>Graphics card load in percent (Windows "GPU Engine" counters), null where not available.</summary>
    double? GpuPercent();
}

public sealed partial class SystemMetrics : ISystemMetrics, IDisposable
{
    private (long Idle, long Total)? _lastCpu;
    private readonly object _gpuLock = new();
    private nint _pdhQuery;
    private nint _pdhCounter;
    private bool _pdhFailed;
    private HardwareInfo? _hardware;

    public HardwareInfo Hardware() => _hardware ??= Probe();

    public (double TotalGb, double AvailableGb)? Memory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys / Gb, status.AvailPhys / Gb) : null;
            }
            if (File.Exists("/proc/meminfo"))
            {
                var info = File.ReadAllLines("/proc/meminfo").Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0], p => double.Parse(p[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024);
                return (info["MemTotal"] / Gb, info.GetValueOrDefault("MemAvailable", info["MemFree"]) / Gb);
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or KeyNotFoundException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public double? CpuPercent()
    {
        (long Idle, long Total)? now = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                    now = (idle, kernel + user); // kernel time includes idle time
            }
            else if (File.Exists("/proc/stat"))
            {
                var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                    .Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                now = (parts[3] + (parts.Length > 4 ? parts[4] : 0), parts.Sum());
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
        }
        if (now is null)
            return null;
        var previous = _lastCpu;
        _lastCpu = now;
        if (previous is null || now.Value.Total <= previous.Value.Total)
            return null;
        var busy = 1 - (double)(now.Value.Idle - previous.Value.Idle) / (now.Value.Total - previous.Value.Total);
        return Math.Round(Math.Clamp(busy * 100, 0, 100), 1);
    }

    public double? GpuPercent()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        lock (_gpuLock)
            return ReadGpu();
    }

    [SupportedOSPlatform("windows")]
    private double? ReadGpu()
    {
        if (_pdhFailed)
            return null;
        try
        {
            if (_pdhQuery == 0)
            {
                if (PdhOpenQuery(null, 0, out _pdhQuery) != 0
                    || PdhAddEnglishCounter(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", 0, out _pdhCounter) != 0)
                {
                    _pdhFailed = true;
                    return null;
                }
                PdhCollectQueryData(_pdhQuery);
                return null; // rate counters need two samples
            }
            if (PdhCollectQueryData(_pdhQuery) != 0)
                return null;
            uint size = 0;
            var status = PdhGetFormattedCounterArray(_pdhCounter, PdhFmtDouble, ref size, out var count, 0);
            if (status != PdhMoreData || size == 0)
                return count == 0 ? 0 : null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_pdhCounter, PdhFmtDouble, ref size, out count, buffer) != 0)
                    return null;
                // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer (8), CStatus (4) + padding (4), double (8)
                var itemSize = IntPtr.Size == 8 ? 24 : 16;
                var byEngineType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                {
                    var item = buffer + i * itemSize;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    var cstatus = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                    if (cstatus > 1)
                        continue;
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size == 8 ? 16 : 8));
                    var type = name.Contains("engtype_", StringComparison.OrdinalIgnoreCase) ? name[(name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase) + 8)..] : "other";
                    byEngineType[type] = byEngineType.GetValueOrDefault(type) + value;
                }
                return byEngineType.Count == 0 ? 0 : Math.Round(Math.Clamp(byEngineType.Values.Max(), 0, 100), 1);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            _pdhFailed = true;
            return null;
        }
    }

    private HardwareInfo Probe()
    {
        var memory = Memory();
        var total = memory?.TotalGb ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / Gb;
        string? cpu = null;
        var gpus = new List<GpuInfo>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
                gpus.AddRange(WindowsGpus());
            }
            else if (File.Exists("/proc/cpuinfo"))
            {
                cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        double? freeDisk = null;
        try
        {
            var models = Environment.GetEnvironmentVariable("OLLAMA_MODELS")
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
            var root = Path.GetPathRoot(Path.GetFullPath(models));
            if (!string.IsNullOrEmpty(root))
                freeDisk = Math.Round(new DriveInfo(root).AvailableFreeSpace / Gb, 1);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        return new HardwareInfo(Math.Round(total, 1), cpu?.Trim(), Environment.ProcessorCount, gpus, freeDisk, RuntimeInformation.OSDescription);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<GpuInfo> WindowsGpus()
    {
        using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (classKey is null)
            yield break;
        foreach (var sub in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var key = classKey.OpenSubKey(sub);
            if (key?.GetValue("DriverDesc") is not string name || IsVirtual(name))
                continue;
            double? vram = key.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long q => q,
                byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                _ => key.GetValue("HardwareInformation.MemorySize") switch
                {
                    int d => (uint)d,
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => null,
                },
            };
            var vramGb = vram is > 0 ? Math.Round(vram.Value / Gb, 1) : (double?)null;
            yield return new GpuInfo(name, vramGb, IsDiscrete(name, vramGb));
        }
    }

    private static bool IsVirtual(string name) =>
        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dedicated cards Ollama can use for speed; integrated graphics (Intel UHD/Iris/Arc Graphics, AMD Radeon Graphics) share the system memory.</summary>
    public static bool IsDiscrete(string name, double? vramGb)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("QUADRO") || n.Contains("RTX"))
            return true;
        if (n.Contains("RADEON RX") || n.Contains("RADEON PRO"))
            return true;
        if (n.Contains("INTEL") && (System.Text.RegularExpressions.Regex.IsMatch(n, @"ARC\(TM\) [AB]\d") || System.Text.RegularExpressions.Regex.IsMatch(n, @"ARC [AB]\d")))
            return true;
        return false;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && _pdhQuery != 0)
            PdhCloseQuery(_pdhQuery);
    }

    private const double Gb = 1024d * 1024 * 1024;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string counterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
