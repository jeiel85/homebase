using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Dashboard;

/// <summary>How a device on the list is connected to this PC (it decides what can be shown).</summary>
internal enum DeviceRole
{
    /// <summary>This PC.</summary>
    Self,
    /// <summary>A PC this one linked to (this PC holds its token: live WebSocket).</summary>
    Hub,
    /// <summary>A PC that linked to this one (it shares its live view on request).</summary>
    LinkedHere,
    /// <summary>A phone paired with this PC.</summary>
    Phone,
    /// <summary>A device seen only through a linked PC (its last report, no live view).</summary>
    ViaHub,
}

/// <summary>One entry of the dashboard's device list (DECISIONS M-44).</summary>
internal sealed record DeviceItem(
    string Key,
    DeviceRole Role,
    string Kind,
    string Name,
    string? Model,
    bool Online,
    DateTimeOffset? LastSeen,
    DeviceStatus? Status,
    string? LiveTarget,
    string? Via = null,
    LocalDeviceInfo? Local = null,
    string? Error = null,
    int Phones = 0)
{
    public bool IsPc => Kind == DeviceKind.Pc;
}

/// <summary>Turns the Agent's <c>devices.overview</c> into the list the dashboard shows.</summary>
internal static class DeviceList
{
    public const string SelfKey = "self";

    /// <summary>
    /// This PC first, then PCs (linked either way), then phones; online before offline, then by name.
    /// A device paired here and also listed by a linked PC appears once (the copy that can show more).
    /// </summary>
    public static IReadOnlyList<DeviceItem> Build(DeviceOverview? overview, string selfName)
    {
        var items = new List<DeviceItem>
        {
            new(SelfKey, DeviceRole.Self, DeviceKind.Pc, selfName, null, true, null, overview?.Here.Hub.Status,
                Core.Mobile.LiveTarget.Self),
        };
        if (overview is null) return items;

        var local = (overview.Local ?? Array.Empty<LocalDeviceInfo>()).ToDictionary(l => l.DeviceId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Seen(DeviceKind.Pc, selfName) };

        // PCs this one linked to come first among the PCs: they open a real live view.
        foreach (var h in overview.Hubs)
        {
            if (!seen.Add(Seen(DeviceKind.Pc, h.PcName))) continue;
            items.Add(new DeviceItem("hub:" + h.PcId, DeviceRole.Hub, DeviceKind.Pc, h.PcName, null, h.Online, h.LastContactAt,
                h.Status, Core.Mobile.LiveTarget.Hub(h.PcId), Error: h.Error,
                Phones: h.Devices.Count(d => d.Kind == DeviceKind.Phone && d.DeviceId != h.SelfDeviceId)));
        }
        foreach (var d in overview.Here.Devices)
        {
            if (!seen.Add(Seen(d.Kind, d.Name))) continue;
            local.TryGetValue(d.DeviceId, out var info);
            var pc = d.Kind == DeviceKind.Pc;
            items.Add(new DeviceItem("device:" + d.DeviceId, pc ? DeviceRole.LinkedHere : DeviceRole.Phone, d.Kind, d.Name, d.Model,
                d.Online, d.LastSeenAt, d.Status, pc ? Core.Mobile.LiveTarget.Device(d.DeviceId) : null, Local: info));
        }
        foreach (var h in overview.Hubs)
        {
            foreach (var d in h.Devices)
            {
                if (d.DeviceId == h.SelfDeviceId || !seen.Add(Seen(d.Kind, d.Name))) continue;
                items.Add(new DeviceItem($"via:{h.PcId}:{d.DeviceId}", DeviceRole.ViaHub, d.Kind, d.Name, d.Model,
                    d.Online && h.Online, d.LastSeenAt, d.Status, null, Via: h.PcName));
            }
        }
        return items
            .OrderBy(i => i.Role == DeviceRole.Self ? 0 : i.IsPc ? 1 : 2)
            .ThenBy(i => i.Online ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string Seen(string kind, string name) => kind + "|" + name.Trim();

    /// <summary>The one line under a device's name on the list.</summary>
    public static string Summary(DeviceItem d)
    {
        var s = d.Status;
        if (d.Role == DeviceRole.Hub && !d.Online)
            return d.Error == "rejected" ? L.DevHubRejected : d.LastSeen is { } at ? L.DevSeen(Fmt.Ago(at, DateTimeOffset.UtcNow)) : L.DevHubConnecting;
        if (s is null) return d.Online ? L.DevOnline : d.LastSeen is { } seen ? L.DevSeen(Fmt.Ago(seen, DateTimeOffset.UtcNow)) : L.DevNoReport;
        var parts = new List<string>();
        if (!d.IsPc)
        {
            if (s.Battery is { } b) parts.Add(L.ListBattery(b, s.Charging == true));
            if (Network(s) is { } n) parts.Add(n);
        }
        else
        {
            if (s.CpuPct is { } cpu) parts.Add("CPU " + Fmt.Pct(cpu));
            if (s.MemUsedBytes is { } used && s.MemTotalBytes is > 0 and var total) parts.Add("RAM " + Fmt.Pct(100.0 * used / total));
        }
        if (!d.Online) parts.Insert(0, d.LastSeen is { } at ? Fmt.Ago(at, DateTimeOffset.UtcNow) : L.DevOffline);
        return parts.Count == 0 ? (d.Online ? L.DevOnline : L.DevOffline) : string.Join(" · ", parts);
    }

    public static string? Network(DeviceStatus s) => s.Network switch
    {
        "wifi" => "Wi-Fi",
        "cellular" => L.DetCellular,
        "ethernet" => L.DetEthernet,
        null or "" => null,
        var other => other,
    };
}

/// <summary>The last 60 readings of one PC, for the traces under each gauge (kept while the window lives).</summary>
internal sealed class History
{
    public const int Capacity = 60;

    private DateTimeOffset _last;
    public readonly List<double> Cpu = new(), Mem = new(), Gpu = new(), Io = new(), Rx = new(), Tx = new(), Ping = new(), Temp = new();

    public void Add(LiveSnapshot s)
    {
        if (s.Ts <= _last) return; // the same snapshot asked for twice
        _last = s.Ts;
        var sys = s.System;
        Push(Cpu, sys.CpuPct);
        Push(Mem, sys.MemTotalBytes > 0 ? 100.0 * sys.MemUsedBytes / sys.MemTotalBytes : 0);
        Push(Io, sys.IoBps);
        Push(Rx, sys.NetRxBps);
        Push(Tx, sys.NetTxBps);
        if (s.Hardware is not { } hw) return;
        if (hw.Gpus.FirstOrDefault()?.LoadPct is { } g) Push(Gpu, g);
        if (hw.Ping?.LastMs is { } p) Push(Ping, p);
        if ((hw.CpuTempC ?? hw.BoardTempC) is { } t) Push(Temp, t);
    }

    private static void Push(List<double> list, double v)
    {
        list.Add(v);
        if (list.Count > Capacity) list.RemoveAt(0);
    }
}
