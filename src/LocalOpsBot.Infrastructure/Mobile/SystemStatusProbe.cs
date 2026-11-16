using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// This PC's state for the device list (DECISIONS M-27): CPU from the change in <c>GetSystemTimes</c>
/// since the previous call, memory from <c>GlobalMemoryStatusEx</c>, uptime. Three cheap system calls —
/// it never starts the per-process sampler, so "collect only while watched" (M-5) holds.
/// The first call has no CPU value (nothing to compare with yet).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemStatusProbe
{
    private readonly object _gate = new();
    private (long Idle, long Total)? _prev;

    public static string AgentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.0.0";

    public DeviceStatus Read()
    {
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user; // kernel time includes idle time
            lock (_gate)
            {
                if (_prev is { } p && total > p.Total)
                    cpu = Math.Round(Math.Clamp(100d * (1 - (double)(idle - p.Idle) / (total - p.Total)), 0, 100), 1);
                _prev = (idle, total);
            }
        }

        long? used = null, totalMem = null;
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            totalMem = (long)mem.TotalPhys;
            used = (long)(mem.TotalPhys - mem.AvailPhys);
        }

        return new DeviceStatus(
            AppVersion: AgentVersion,
            CpuPct: cpu,
            MemUsedBytes: used,
            MemTotalBytes: totalMem,
            UptimeSec: Environment.TickCount64 / 1000,
            Os: OsName);
    }

    /// <summary>The same, as a report to a hub this PC is linked to (DECISIONS M-29).</summary>
    public PresenceReport Report(IReadOnlyList<string>? pushHashes)
    {
        var s = Read();
        return new PresenceReport(MobileProtocol.Schema, DeviceKind.Pc, s.AppVersion,
            CpuPct: s.CpuPct, MemUsedBytes: s.MemUsedBytes, MemTotalBytes: s.MemTotalBytes, UptimeSec: s.UptimeSec,
            PushHashes: pushHashes is { Count: > 0 } ? pushHashes.Take(PresenceReport.MaxPushHashes).ToList() : null,
            Os: s.Os);
    }

    /// <summary>"Windows 11 Pro 24H2"-style name from the registry, else the runtime's description (M-44).</summary>
    public static string OsName { get; } = ReadOsName();

    private static string ReadOsName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;
            var build = Environment.OSVersion.Version.Build;
            if (!string.IsNullOrWhiteSpace(product))
            {
                // ProductName still says "Windows 10" on Windows 11 (build 22000 and later).
                if (build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
                var name = string.IsNullOrWhiteSpace(display) ? product : $"{product} {display}";
                return name.Length <= 64 ? name : name[..64];
            }
        }
        catch (Exception) { }
        var fallback = RuntimeInformation.OSDescription;
        return fallback.Length <= 64 ? fallback : fallback[..64];
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
