using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// Any vendor's GPU without vendor tools or kernel drivers — the sources Task Manager reads: the graphics
/// kernel (D3DKMT) names the adapters and gives their memory sizes, temperature, memory clock, power share
/// and fan; the "GPU Engine" and "GPU Adapter Memory" performance counters give the load and memory in use.
/// Not thread-safe: one owner calls <see cref="Sample"/>.
/// Adapted from PhoneBox (https://github.com/ersinkoc/phonebox, <c>agent/PhoneBox.Agent/WindowsGpu.cs</c>),
/// MIT License, Copyright (c) 2026 Ersin KOÇ (see THIRD-PARTY-NOTICES.txt).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsGpuReader : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000;

    private sealed record Adapter(string Luid, uint Handle, string Name, string? Vendor, bool Integrated,
        long? DedicatedBytes, long? SharedBytes, double? TempMax, bool HasFan);

    private List<Adapter> _adapters = [];
    private long _enumeratedAt = -1;
    private IntPtr _query, _engine, _dedicated, _shared;
    private bool _primed;

    public WindowsGpuReader()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        // English counter names: the localized ones differ per Windows display language.
        if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine) != 0) _engine = IntPtr.Zero;
        if (PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _dedicated) != 0) _dedicated = IntPtr.Zero;
        if (PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out _shared) != 0) _shared = IntPtr.Zero;
    }

    /// <summary>
    /// One reading of every hardware adapter (software and indirect-display adapters are left out). The load
    /// is a rate, so the first call after construction has none.
    /// </summary>
    public List<GpuReading> Sample()
    {
        // The adapter set rarely changes (driver update, eGPU): re-read it every five minutes.
        if (_enumeratedAt < 0 || Environment.TickCount64 - _enumeratedAt > 300_000) Enumerate();

        Dictionary<string, double> util = [], dedicated = [], shared = [];
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0 && _primed)
        {
            // Load per engine summed over the processes using it; the adapter's load is its busiest engine.
            var engines = new Dictionary<string, double>();
            foreach (var (inst, v) in Read(_engine))
                if (Instance().Match(inst) is { Success: true } m)
                {
                    var key = Luid(m) + "|" + m.Groups[3].Value + "|" + m.Groups[4].Value;
                    engines[key] = engines.GetValueOrDefault(key) + v;
                }
            foreach (var (key, v) in engines)
            {
                var luid = key[..key.IndexOf('|')];
                util[luid] = Math.Max(util.GetValueOrDefault(luid), Math.Min(100, v));
            }
            foreach (var (inst, v) in Read(_dedicated))
                if (Instance().Match(inst) is { Success: true } m) dedicated[Luid(m)] = dedicated.GetValueOrDefault(Luid(m)) + v;
            foreach (var (inst, v) in Read(_shared))
                if (Instance().Match(inst) is { Success: true } m) shared[Luid(m)] = shared.GetValueOrDefault(Luid(m)) + v;
        }
        _primed = _query != IntPtr.Zero; // the first collection of a rate counter has nothing to compare with

        var list = new List<GpuReading>(_adapters.Count);
        foreach (var a in _adapters)
        {
            var perf = PerfData(a.Handle);
            list.Add(new GpuReading(
                a.Name, a.Vendor, a.Integrated,
                LoadPct: util.TryGetValue(a.Luid, out var u) ? Math.Round(u, 1) : null,
                TempC: perf?.Temp,
                TempMaxC: a.TempMax,
                VramUsedBytes: dedicated.TryGetValue(a.Luid, out var d) ? (long)d : null,
                VramTotalBytes: a.DedicatedBytes,
                SharedUsedBytes: shared.TryGetValue(a.Luid, out var s) ? (long)s : null,
                SharedTotalBytes: a.SharedBytes,
                PowerW: null,
                PowerLimitW: null,
                PowerPct: perf?.PowerPct,
                FanPct: null,
                FanRpm: a.HasFan ? perf?.FanRpm : null,
                ClockMhz: null,
                MemClockMhz: perf?.MemClock,
                Source: "windows"));
        }
        return list;
    }

    private void Enumerate()
    {
        _enumeratedAt = Environment.TickCount64;
        CloseAdapters();
        var buf = new AdapterInfo[32];
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        uint count;
        try
        {
            var en = new EnumAdapters2 { NumAdapters = (uint)buf.Length, pAdapters = pin.AddrOfPinnedObject() };
            if (D3DKMTEnumAdapters2(ref en) != 0) return;
            count = Math.Min(en.NumAdapters, (uint)buf.Length);
        }
        finally { pin.Free(); }

        for (var i = 0; i < count; i++)
        {
            var a = buf[i];
            var typeBuf = new byte[4];
            Query(a.hAdapter, 15 /* KMTQAITYPE_ADAPTERTYPE */, typeBuf);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(typeBuf);
            var ids = new byte[28];
            Query(a.hAdapter, 31 /* KMTQAITYPE_PHYSICALADAPTERDEVICEIDS */, ids);
            var vendorId = BinaryPrimitives.ReadUInt32LittleEndian(ids.AsSpan(4));
            var renders = (type & 1u) != 0;
            var software = (type & (1u << 2)) != 0;
            var indirect = (type & (1u << 6)) != 0;
            if (software || indirect || !renders || vendorId == 0x1414 /* Microsoft Basic Render / Remote Display */)
            {
                Close(a.hAdapter);
                continue;
            }

            var name = "GPU";
            var reg = new byte[4 * 260 * 2];
            if (Query(a.hAdapter, 8 /* KMTQAITYPE_ADAPTERREGISTRYINFO */, reg) == 0)
            {
                var adapterString = MemoryMarshal.Cast<byte, char>(reg.AsSpan(0, 260 * 2));
                var end = adapterString.IndexOf((char)0);
                var n = new string(end < 0 ? adapterString : adapterString[..end]).Trim();
                if (n.Length > 0) name = n;
            }

            var seg = new byte[24];
            var segOk = Query(a.hAdapter, 3 /* KMTQAITYPE_GETSEGMENTSIZE */, seg) == 0;
            var dedicatedBytes = BinaryPrimitives.ReadUInt64LittleEndian(seg);
            var sharedBytes = BinaryPrimitives.ReadUInt64LittleEndian(seg.AsSpan(16));
            var caps = new byte[40];
            var capsOk = Query(a.hAdapter, 63 /* KMTQAITYPE_ADAPTERPERFDATA_CAPS */, caps) == 0;
            var maxFan = BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(24));
            var tempMax = BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(28)) / 10.0;
            var hybridIntegrated = (type & (1u << 5)) != 0;

            _adapters.Add(new Adapter(
                Luid: $"{(uint)a.LuidHigh:x8}_{a.LuidLow:x8}",
                Handle: a.hAdapter,
                Name: name,
                Vendor: GpuSources.VendorOf(vendorId) ?? GpuSources.VendorOf(name),
                Integrated: hybridIntegrated || GpuSources.LooksIntegrated(name),
                DedicatedBytes: segOk && dedicatedBytes > 0 ? (long)dedicatedBytes : null,
                SharedBytes: segOk && sharedBytes > 0 ? (long)sharedBytes : null,
                TempMax: capsOk && tempMax > 0 ? tempMax : null,
                HasFan: capsOk && maxFan > 0));
        }
    }

    private sealed record Perf(double? Temp, double? MemClock, double? PowerPct, double? FanRpm);

    /// <summary>D3DKMT_ADAPTER_PERFDATA (WDDM 2.4+): temperature in tenths of a degree, power in tenths of a percent of the board limit.</summary>
    private static Perf? PerfData(uint handle)
    {
        var buf = new byte[64];
        if (Query(handle, 62 /* KMTQAITYPE_ADAPTERPERFDATA */, buf) != 0) return null;
        var memHz = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8));
        var fan = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(48));
        var power = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(52));
        var temp = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(56));
        return new Perf(
            temp is > 0 and < 2000 ? Math.Round(temp / 10.0, 1) : null,
            memHz > 0 ? Math.Round(memHz / 1e6) : null,
            power is > 0 and <= 2000 ? Math.Round(power / 10.0, 1) : null,
            fan);
    }

    private static string Luid(Match m) => m.Groups[1].Value.ToLowerInvariant() + "_" + m.Groups[2].Value.ToLowerInvariant();

    private static List<(string Instance, double Value)> Read(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, IntPtr.Zero) != PdhMoreData || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, buf) != 0) return list;
            var stride = Marshal.SizeOf<FmtItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<FmtItem>(buf + i * stride);
                // Instances of processes that just started have no valid value yet.
                if (item.CStatus is not (0 or 1) || item.Name == IntPtr.Zero) continue;
                list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private static uint Query(uint handle, int type, byte[] data)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var q = new QueryAdapterInfo { hAdapter = handle, Type = type, pPrivateDriverData = pin.AddrOfPinnedObject(), PrivateDriverDataSize = (uint)data.Length };
            return D3DKMTQueryAdapterInfo(ref q);
        }
        finally { pin.Free(); }
    }

    private static void Close(uint handle)
    {
        var h = handle;
        D3DKMTCloseAdapter(ref h);
    }

    private void CloseAdapters()
    {
        foreach (var a in _adapters) Close(a.Handle);
        _adapters = [];
    }

    public void Dispose()
    {
        CloseAdapters();
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    // pid_1234_luid_0x00000000_0x0001C621_phys_0_eng_3_engtype_3D  /  luid_0x00000000_0x0001C621_phys_0
    [GeneratedRegex(@"luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_(\d+)(?:_eng_(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex Instance();

    [StructLayout(LayoutKind.Sequential)] private struct AdapterInfo { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int PrecisePresentRegionsPreferred; }
    [StructLayout(LayoutKind.Sequential)] private struct EnumAdapters2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
    [StructLayout(LayoutKind.Sequential)] private struct FmtItem { public IntPtr Name; public uint CStatus; public double Value; }

    [DllImport("gdi32.dll")] private static extern uint D3DKMTEnumAdapters2(ref EnumAdapters2 e);
    [DllImport("gdi32.dll")] private static extern uint D3DKMTQueryAdapterInfo(ref QueryAdapterInfo q);
    [DllImport("gdi32.dll")] private static extern uint D3DKMTCloseAdapter(ref uint h);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
}
