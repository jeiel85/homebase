using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// Per-process network byte counters from the <c>Microsoft-Windows-Kernel-Network</c> ETW provider,
/// consumed in a private real-time session (not the shared "NT Kernel Logger").
///
/// Hand-rolled P/Invoke instead of TraceEvent on purpose: the callback reads two UInt32 fields
/// (PID, size) at fixed offsets of the event payload, so there is no manifest parsing and no
/// allocation per packet, and no extra assembly is loaded into the service. Requires
/// administrator/LocalSystem; when it can't start, callers fall back to "no per-process network".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EtwNetworkTracker : IDisposable
{
    private const string SessionName = "Homebase-MobileNet";
    private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;

    // Kernel-Network event ids. Every one of these starts its payload with PID (UInt32), size (UInt32).
    private static bool IsSend(ushort id) => id is 10 or 26 or 42 or 58;
    private static bool IsReceive(ushort id) => id is 11 or 27 or 43 or 59;

    private const int PropertiesSize = 120;          // sizeof(EVENT_TRACE_PROPERTIES), x64
    private const int PropertiesBufferSize = PropertiesSize + 512;
    private const int LogfileSize = 448;             // sizeof(EVENT_TRACE_LOGFILEW), x64
    private const ulong InvalidTraceHandle = ulong.MaxValue;

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, (long Rx, long Tx)> _totals = new();
    private readonly EventRecordCallback _callback;
    private ulong _session;
    private ulong _trace = InvalidTraceHandle;
    private IntPtr _logfile, _loggerName;
    private Thread? _thread;

    public EtwNetworkTracker(ILogger logger)
    {
        _logger = logger;
        _callback = OnEvent; // keep the delegate alive for the native callback
    }

    public bool IsRunning { get; private set; }

    public bool Start()
    {
        if (IsRunning) return true;
        if (!Environment.Is64BitProcess)
        {
            _logger.LogWarning("Per-process network tracing needs a 64-bit Agent; skipping.");
            return false;
        }

        var props = AllocProperties();
        try
        {
            var rc = StartTraceW(out _session, SessionName, props);
            if (rc == ErrorAlreadyExists)
            {
                // A session left over from a crashed Agent: stop it and take over.
                ControlTraceW(0, SessionName, ResetProperties(props), ControlStop);
                rc = StartTraceW(out _session, SessionName, ResetProperties(props));
            }
            if (rc != 0)
            {
                _logger.LogWarning("ETW network session could not start (error {Code}); per-process network is unavailable.", rc);
                return false;
            }

            var provider = KernelNetwork;
            rc = EnableTraceEx2(_session, ref provider, 1 /*ENABLE*/, 5 /*verbose*/, KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
            if (rc != 0)
            {
                _logger.LogWarning("Enabling the Kernel-Network provider failed (error {Code}).", rc);
                StopSession();
                return false;
            }

            _loggerName = Marshal.StringToHGlobalUni(SessionName);
            _logfile = Marshal.AllocHGlobal(LogfileSize);
            ZeroMemory(_logfile, LogfileSize);
            Marshal.WriteIntPtr(_logfile, 8, _loggerName);                        // LoggerName
            Marshal.WriteInt32(_logfile, 28, 0x100 | 0x10000000);                 // REAL_TIME | EVENT_RECORD
            Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback));
            _trace = OpenTraceW(_logfile);
            if (_trace == InvalidTraceHandle)
            {
                _logger.LogWarning("OpenTrace failed (error {Code}).", Marshal.GetLastWin32Error());
                StopSession();
                return false;
            }

            lock (_gate) _totals.Clear();
            _thread = new Thread(() =>
            {
                var handles = new[] { _trace };
                ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero); // blocks until the session stops
            }) { IsBackground = true, Name = "Homebase ETW network" };
            _thread.Start();
            IsRunning = true;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        StopSession();
        if (_trace != InvalidTraceHandle) { CloseTrace(_trace); _trace = InvalidTraceHandle; }
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        lock (_gate) _totals.Clear();
    }

    /// <summary>Cumulative bytes per PID since <see cref="Start"/>.</summary>
    public Dictionary<int, (long Rx, long Tx)> Snapshot()
    {
        lock (_gate) return new Dictionary<int, (long, long)>(_totals);
    }

    /// <summary>Drops counters of processes that no longer exist so a reused PID starts from zero.</summary>
    public void Prune(IReadOnlySet<int> livePids)
    {
        lock (_gate)
        {
            foreach (var pid in _totals.Keys.Where(p => !livePids.Contains(p)).ToList())
                _totals.Remove(pid);
        }
    }

    public void Dispose() => Stop();

    private void OnEvent(IntPtr record)
    {
        // EVENT_RECORD: EventHeader.EventDescriptor.Id @40, UserDataLength @86, UserData @96.
        var id = (ushort)Marshal.ReadInt16(record, 40);
        bool send = IsSend(id);
        if (!send && !IsReceive(id)) return;
        if ((ushort)Marshal.ReadInt16(record, 86) < 8) return;
        var data = Marshal.ReadIntPtr(record, 96);
        var pid = Marshal.ReadInt32(data, 0);
        var size = (uint)Marshal.ReadInt32(data, 4);
        lock (_gate)
        {
            _totals.TryGetValue(pid, out var t);
            _totals[pid] = send ? (t.Rx, t.Tx + size) : (t.Rx + size, t.Tx);
        }
    }

    private void StopSession()
    {
        if (_session == 0) return;
        var props = AllocProperties();
        try { ControlTraceW(_session, null, props, ControlStop); }
        finally { Marshal.FreeHGlobal(props); _session = 0; }
    }

    private static IntPtr AllocProperties() => ResetProperties(Marshal.AllocHGlobal(PropertiesBufferSize));

    private static IntPtr ResetProperties(IntPtr p)
    {
        ZeroMemory(p, PropertiesBufferSize);
        Marshal.WriteInt32(p, 0, PropertiesBufferSize);   // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                     // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, 0x00020000);            // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);                    // BufferSize (KB)
        Marshal.WriteInt32(p, 52, 4);                     // MinimumBuffers
        Marshal.WriteInt32(p, 56, 16);                    // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x100);                 // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);                     // FlushTimer (s) — counters stay ~1 s fresh
        Marshal.WriteInt32(p, 116, PropertiesSize);       // LoggerNameOffset
        return p;
    }

    private static void ZeroMemory(IntPtr p, int size)
    {
        for (var i = 0; i < size; i += 8) Marshal.WriteInt64(p, i, 0);
    }

    private const uint ErrorAlreadyExists = 183;
    private const uint ControlStop = 1;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);
}
