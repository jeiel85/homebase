using System.Globalization;
using System.Text.RegularExpressions;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Hardware readings for the live view (DECISIONS M-34). Like <see cref="IResourceSampler"/> it works only
/// between <see cref="Start"/> and <see cref="Stop"/>, so nothing (no GPU counters, no nvidia-smi, no
/// pings) runs while no phone is watching.
/// </summary>
public interface IHardwareSampler
{
    void Start();

    void Stop();

    /// <summary>The latest readings, or null before the first one after <see cref="Start"/>.</summary>
    HardwareInfo? Current { get; }
}

/// <summary>
/// Merges GPU readings from several sources (nvidia-smi, the Windows graphics kernel). The same card can
/// come from more than one; entries are matched by name, the earlier source wins and a later one only
/// fills what it left empty.
/// Adapted from PhoneBox (https://github.com/ersinkoc/phonebox, <c>agent/PhoneBox.Agent/Gpus.cs</c>),
/// MIT License, Copyright (c) 2026 Ersin KOÇ (see THIRD-PARTY-NOTICES.txt).
/// </summary>
public static partial class GpuSources
{
    private const long MiB = 1024L * 1024;

    /// <summary>Merges the sources (highest priority first); the main GPU first: discrete before integrated, then most VRAM.</summary>
    public static List<GpuReading> Compose(params IReadOnlyList<GpuReading>?[] sources)
    {
        var list = new List<GpuReading>();
        foreach (var src in sources)
        {
            if (src is null) continue;
            var taken = new HashSet<int>(); // two identical cards: each entry is matched once per source
            foreach (var g in src)
            {
                var key = Key(g.Name);
                var i = -1;
                for (var j = 0; j < list.Count && i < 0; j++)
                    if (!taken.Contains(j) && Key(list[j].Name) == key) i = j;
                if (i < 0) { list.Add(g); taken.Add(list.Count - 1); }
                else { list[i] = Merge(list[i], g); taken.Add(i); }
            }
        }
        return list.Select((g, i) => (g, i))
            .OrderBy(t => t.g.Integrated)
            .ThenByDescending(t => t.g.VramTotalBytes ?? 0)
            .ThenBy(t => t.i)
            .Select(t => t.g)
            .ToList();
    }

    /// <summary>Fields of <paramref name="a"/> win; <paramref name="b"/> fills the gaps.</summary>
    public static GpuReading Merge(GpuReading a, GpuReading b) => a with
    {
        Vendor = a.Vendor ?? b.Vendor,
        Integrated = a.Integrated || b.Integrated,
        LoadPct = a.LoadPct ?? b.LoadPct,
        TempC = a.TempC ?? b.TempC,
        TempMaxC = a.TempMaxC ?? b.TempMaxC,
        VramUsedBytes = a.VramUsedBytes ?? b.VramUsedBytes,
        VramTotalBytes = a.VramTotalBytes ?? b.VramTotalBytes,
        SharedUsedBytes = a.SharedUsedBytes ?? b.SharedUsedBytes,
        SharedTotalBytes = a.SharedTotalBytes ?? b.SharedTotalBytes,
        PowerW = a.PowerW ?? b.PowerW,
        PowerLimitW = a.PowerLimitW ?? b.PowerLimitW,
        PowerPct = a.PowerPct ?? b.PowerPct,
        FanPct = a.FanPct ?? b.FanPct,
        FanRpm = a.FanRpm ?? b.FanRpm,
        ClockMhz = a.ClockMhz ?? b.ClockMhz,
        MemClockMhz = a.MemClockMhz ?? b.MemClockMhz,
        Source = a.Source is null ? b.Source
            : b.Source is null || a.Source.Split('+').Contains(b.Source) ? a.Source
            : a.Source + "+" + b.Source,
    };

    /// <summary>"AMD Radeon(TM) Graphics" and "AMD Radeon Graphics" are the same card.</summary>
    public static string Key(string name) =>
        NonAlnum().Replace(name.ToLowerInvariant().Replace("(tm)", "").Replace("(r)", ""), "");

    public static string? VendorOf(string name) =>
        name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) || name.Contains("geforce", StringComparison.OrdinalIgnoreCase) ? "nvidia"
        : name.Contains("amd", StringComparison.OrdinalIgnoreCase) || name.Contains("radeon", StringComparison.OrdinalIgnoreCase) ? "amd"
        : name.Contains("intel", StringComparison.OrdinalIgnoreCase) ? "intel"
        : null;

    public static string? VendorOf(uint pciVendor) => pciVendor switch
    {
        0x10de => "nvidia",
        0x1002 or 0x1022 => "amd",
        0x8086 => "intel",
        0x5143 => "qualcomm",
        _ => null,
    };

    /// <summary>
    /// Integrated graphics by name: AMD APUs are "Radeon(TM) Graphics", "Radeon 780M Graphics", "Radeon RX Vega 11 Graphics";
    /// Intel's are everything but the Arc A/B cards ("Arc(TM) Graphics" and "Arc(TM) 140V GPU" are integrated too).
    /// </summary>
    public static bool LooksIntegrated(string name)
    {
        if (name.Contains("intel", StringComparison.OrdinalIgnoreCase))
            return !IntelDiscrete().IsMatch(name);
        if (name.Contains("radeon", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AMD", StringComparison.OrdinalIgnoreCase))
            return name.TrimEnd().EndsWith("Graphics", StringComparison.OrdinalIgnoreCase) && !AmdDiscrete().IsMatch(name);
        return false;
    }

    /// <summary>Query fields of the streaming nvidia-smi call, in this order.</summary>
    public const string NvidiaQuery =
        "index,utilization.gpu,temperature.gpu,power.draw,power.limit,memory.used,memory.total,fan.speed,clocks.gr,clocks.mem";

    /// <summary>
    /// One line of <c>--query-gpu=<see cref="NvidiaQuery"/> --format=csv,noheader,nounits</c>.
    /// Fields nvidia-smi cannot read ("[N/A]", "[Not Supported]") stay null, not zero.
    /// </summary>
    public static (int Index, GpuReading Gpu)? ParseNvidiaLine(string line, IReadOnlyDictionary<int, string> names)
    {
        var f = line.Split(',')
            .Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null)
            .ToArray();
        if (f.Length < 9 || f[0] is not { } idx || idx < 0) return null;
        var name = names.TryGetValue((int)idx, out var n) ? n : "NVIDIA GPU";
        return ((int)idx, new GpuReading(
            name, "nvidia", Integrated: false,
            LoadPct: f[1], TempC: f[2], TempMaxC: null,
            VramUsedBytes: Bytes(f[5]), VramTotalBytes: Bytes(f[6]),
            SharedUsedBytes: null, SharedTotalBytes: null,
            PowerW: f[3], PowerLimitW: f[4], PowerPct: null,
            FanPct: f[7], FanRpm: null,
            ClockMhz: f[8], MemClockMhz: f.Length > 9 ? f[9] : null,
            Source: "nvidia-smi"));

        static long? Bytes(double? mib) => mib is { } m ? (long)Math.Round(m * MiB) : null;
    }

    /// <summary>The <c>--query-gpu=index,name --format=csv,noheader</c> answer.</summary>
    public static Dictionary<int, string> ParseNvidiaNames(string text)
    {
        var map = new Dictionary<int, string>();
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(',', 2).Select(s => s.Trim()).ToArray();
            if (f.Length == 2 && int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && f[1].Length > 0)
                map[i] = f[1];
        }
        return map;
    }

    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlnum();
    [GeneratedRegex(@"Arc(\(TM\))?\s+(Pro\s+)?[AB]\d{2,3}", RegexOptions.IgnoreCase)] private static partial Regex IntelDiscrete();
    [GeneratedRegex(@"\bRX\s+\d{3,4}|\bPro\s+W\d|\bW\d{4}", RegexOptions.IgnoreCase)] private static partial Regex AmdDiscrete();
}

/// <summary>A rolling window of ping results: unanswered pings count as loss and are left out of the average.</summary>
public sealed class PingWindow
{
    private readonly Queue<double?> _samples = new();
    private readonly int _capacity;

    public PingWindow(string host, int capacity = 30)
    {
        Host = host;
        _capacity = Math.Max(1, capacity);
    }

    public string Host { get; }

    public PingStats Add(double? ms)
    {
        _samples.Enqueue(ms);
        while (_samples.Count > _capacity) _samples.Dequeue();
        var ok = _samples.Where(s => s is not null).Select(s => s!.Value).ToList();
        return new PingStats(
            Host,
            ms is { } last ? Math.Round(last, 1) : null,
            ok.Count > 0 ? Math.Round(ok.Average(), 1) : null,
            Math.Round(100.0 * (_samples.Count - ok.Count) / _samples.Count, 1),
            _samples.Count);
    }
}

/// <summary>Picks the panel's CPU and board temperature out of the sensor list.</summary>
public static class PanelTemperatures
{
    // LibreHardwareMonitor's package / die sensors (AMD, Intel); the first present wins.
    private static readonly string[] CpuPackage = ["Core (Tctl/Tdie)", "Tctl/Tdie", "CPU Package", "Tdie", "Tctl", "Core (Tdie)"];

    /// <summary>The CPU package temperature (else the hottest CPU sensor) and the hottest board sensor.</summary>
    public static (double? Cpu, double? Board) From(TemperatureSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Sensors.Count == 0) return (null, null);
        var cpu = snapshot.Sensors.Where(s => s.Kind == "Cpu").ToList();
        double? cpuC = null;
        foreach (var name in CpuPackage)
        {
            var hit = cpu.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) { cpuC = hit.Celsius; break; }
        }
        cpuC ??= cpu.Count > 0 ? cpu.Max(s => s.Celsius) : null;
        var board = snapshot.Sensors.Where(s => s.Kind == "Board").ToList();
        double? boardC = board.Count > 0 ? board.Max(s => s.Celsius) : null;
        return (Round(cpuC), Round(boardC));

        static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
    }
}
