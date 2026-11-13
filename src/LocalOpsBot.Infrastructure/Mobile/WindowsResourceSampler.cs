using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// Cheap per-tick process sampling: one Toolhelp snapshot for names/parents, then per process a
/// PROCESS_QUERY_LIMITED_INFORMATION handle for times, I/O and working set. No WMI, no
/// performance counters (both cost far more per call). Rates are deltas against the previous tick.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsResourceSampler : IResourceSampler, IDisposable
{
    private readonly EtwNetworkTracker _net;
    private readonly object _gate = new();
    private Dictionary<int, Prev> _prev = new();
    private Dictionary<int, (long Rx, long Tx)> _prevNet = new();
    private long _prevTick;
    private (long Idle, long Kernel, long User) _prevSys;
    private (long Rx, long Tx) _prevNics;

    private readonly record struct Prev(long CreateTime, long CpuTime, long IoBytes);

    public WindowsResourceSampler(ILogger<WindowsResourceSampler> logger)
    {
        _net = new EtwNetworkTracker(logger);
    }

    public bool NetPerProcess => _net.IsRunning;

    public void Start()
    {
        lock (_gate)
        {
            _prev.Clear();
            _prevNet.Clear();
            _prevTick = 0;
            _net.Start();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _net.Stop();
            _prev.Clear();
            _prevNet.Clear();
        }
    }

    public (IReadOnlyList<ProcessSample> Processes, SystemTotals System) Sample()
    {
        lock (_gate)
        {
            var tick = Stopwatch.GetTimestamp();
            var elapsedSec = _prevTick == 0 ? 0 : Stopwatch.GetElapsedTime(_prevTick, tick).TotalSeconds;
            var wall100ns = elapsedSec * 10_000_000d * Environment.ProcessorCount;
            var netNow = _net.IsRunning ? _net.Snapshot() : null;

            var samples = new List<ProcessSample>(400);
            var next = new Dictionary<int, Prev>(_prev.Count + 16);
            long ioTotal = 0;

            foreach (var (pid, ppid, name) in EnumerateProcesses())
            {
                long create = 0, cpu = 0, io = 0, mem = 0;
                if (pid != 0)
                {
                    var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            if (GetProcessTimes(h, out create, out _, out var k, out var u)) cpu = k + u;
                            if (GetProcessIoCounters(h, out var ioc)) io = (long)(ioc.ReadTransferCount + ioc.WriteTransferCount);
                            var pmc = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
                            if (K32GetProcessMemoryInfo(h, ref pmc, pmc.cb)) mem = (long)pmc.WorkingSetSize;
                        }
                        finally { CloseHandle(h); }
                    }
                }

                double cpuPct = 0;
                long ioBps = 0;
                if (elapsedSec > 0 && _prev.TryGetValue(pid, out var p) && p.CreateTime == create)
                {
                    cpuPct = wall100ns > 0 ? Math.Clamp((cpu - p.CpuTime) / wall100ns * 100d, 0, 100) : 0;
                    ioBps = (long)(Math.Max(0, io - p.IoBytes) / elapsedSec);
                }
                ioTotal += ioBps;
                next[pid] = new Prev(create, cpu, io);

                long? rx = null, tx = null;
                if (netNow is not null)
                {
                    netNow.TryGetValue(pid, out var n);
                    _prevNet.TryGetValue(pid, out var pn);
                    rx = elapsedSec > 0 ? (long)(Math.Max(0, n.Rx - pn.Rx) / elapsedSec) : 0;
                    tx = elapsedSec > 0 ? (long)(Math.Max(0, n.Tx - pn.Tx) / elapsedSec) : 0;
                }

                samples.Add(new ProcessSample(pid, ppid, name, create,
                    new ResourceUsage(Math.Round(cpuPct, 1), mem, ioBps, rx, tx)));
            }

            if (netNow is not null)
            {
                _net.Prune(next.Keys.ToHashSet());
                _prevNet = netNow;
            }
            _prev = next;
            _prevTick = tick;
            return (samples, SampleSystem(elapsedSec, ioTotal));
        }
    }

    private SystemTotals SampleSystem(double elapsedSec, long ioTotal)
    {
        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var dIdle = idle - _prevSys.Idle;
            var dTotal = (kernel - _prevSys.Kernel) + (user - _prevSys.User); // kernel includes idle
            if (elapsedSec > 0 && dTotal > 0) cpu = Math.Clamp((dTotal - dIdle) * 100d / dTotal, 0, 100);
            _prevSys = (idle, kernel, user);
        }

        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);

        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch (NetworkInformationException) { /* totals stay 0 for this tick */ }

        long rxBps = 0, txBps = 0;
        if (elapsedSec > 0)
        {
            rxBps = (long)(Math.Max(0, rx - _prevNics.Rx) / elapsedSec);
            txBps = (long)(Math.Max(0, tx - _prevNics.Tx) / elapsedSec);
        }
        _prevNics = (rx, tx);

        return new SystemTotals(
            Math.Round(cpu, 1),
            (long)(mem.ullTotalPhys - mem.ullAvailPhys),
            (long)mem.ullTotalPhys,
            ioTotal,
            rxBps,
            txBps,
            Environment.TickCount64 / 1000);
    }

    private static IEnumerable<(int Pid, int ParentPid, string Name)> EnumerateProcesses()
    {
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) yield break;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snap, ref entry)) yield break;
            do
            {
                var exe = entry.szExeFile ?? "";
                var name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
                yield return ((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, name);
            } while (Process32NextW(snap, ref entry));
        }
        finally { CloseHandle(snap); }
    }

    public void Dispose() => _net.Dispose();

    // --- native ---------------------------------------------------------------------------
    private const uint Th32csSnapProcess = 0x2;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
            ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern bool K32GetProcessMemoryInfo(IntPtr h, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
