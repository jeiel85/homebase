using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services.Mobile;

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

/// <summary>
/// One entry of the dashboard's device list (DECISIONS M-46). <c>Name</c> is what the device reports;
/// <c>Alias</c> the group's name for it (M-51), shown in its place (<see cref="Shown"/>). <c>AliasKey</c>
/// is its group key, to set that alias — null while a PC linked here has not said its id yet.
/// <c>TwoWay</c>: a PC linked both ways (M-47); <c>Role</c> is then the way its live view comes now.
/// </summary>
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
    int Phones = 0,
    string? Alias = null,
    string? AliasKey = null,
    string? ViaKey = null,
    bool TwoWay = false)
{
    public bool IsPc => Kind == DeviceKind.Pc;

    /// <summary>The name to show: the alias when set, else the device's own name.</summary>
    public string Shown => Alias ?? Name;
}

/// <summary>Turns the Agent's <c>devices.overview</c> into the list the dashboard shows.</summary>
internal static class DeviceList
{
    public const string SelfKey = "self";

    /// <summary>
    /// This PC first, then PCs (linked either way), then phones; online before offline, then by the name
    /// shown. A PC linked both ways is one entry (M-47), and a device paired here and also listed by a linked
    /// PC appears once (the copy that can show more). With <paramref name="aliases"/>, each device and the PC
    /// it is seen through carry the group's alias for them (M-51).
    /// </summary>
    public static IReadOnlyList<DeviceItem> Build(DeviceOverview? overview, string selfName, DeviceAliasTable? aliases = null)
    {
        var items = Collect(overview, selfName);
        if (aliases is null) return Sorted(items);
        return Sorted(items.Select(i => i with
        {
            Alias = aliases.Resolve(i.AliasKey, i.Kind, i.Name),
            Via = i.Via is { } via ? aliases.Show(i.ViaKey, DeviceKind.Pc, via) : null,
        }));
    }

    /// <summary>The group key of a device on a PC's device list (M-47, M-51): its pcId for a PC, name and model for a phone.</summary>
    public static string? KeyOf(DeviceEntry d) => d.Kind == DeviceKind.Pc
        ? d.PcId is { Length: > 0 } pcId ? GroupKeys.Pc(pcId) : null
        : GroupKeys.PhoneByName(d.Name, d.Model);

    private static List<DeviceItem> Sorted(IEnumerable<DeviceItem> items) => items
        .OrderBy(i => i.Role == DeviceRole.Self ? 0 : i.IsPc ? 1 : 2)
        .ThenBy(i => i.Online ? 0 : 1)
        .ThenBy(i => i.Shown, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static List<DeviceItem> Collect(DeviceOverview? overview, string selfName)
    {
        var items = new List<DeviceItem>
        {
            new(SelfKey, DeviceRole.Self, DeviceKind.Pc, selfName, null, true, null, overview?.Here.Hub.Status,
                Core.Mobile.LiveTarget.Self, AliasKey: overview?.Here.Hub.PcId is { Length: > 0 } self ? GroupKeys.Pc(self) : null),
        };
        if (overview is null) return items;

        var local = (overview.Local ?? Array.Empty<LocalDeviceInfo>()).ToDictionary(l => l.DeviceId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Seen(DeviceKind.Pc, selfName) };

        // A group links PCs both ways (M-47): one entry per PC, whichever way it is linked.
        foreach (var link in LinkedPcs.Pair(overview.Hubs, overview.Here.Devices))
        {
            var names = link.Here.Select(d => d.Name).Prepend(link.Name).Select(n => Seen(DeviceKind.Pc, n)).ToList();
            if (names.Contains(Seen(DeviceKind.Pc, selfName))) continue; // this PC itself, linked to its own code
            seen.UnionWith(names);
            items.Add(PcItem(link, local));
        }
        // One entry per name: the copy in use wins over an old one a reinstall left behind (M-71), which
        // the store lists first because it was paired first.
        foreach (var d in Freshest(overview.Here.Devices.Where(d => d.Kind != DeviceKind.Pc)))
        {
            if (!seen.Add(Seen(d.Kind, d.Name))) continue;
            local.TryGetValue(d.DeviceId, out var info);
            items.Add(new DeviceItem("device:" + d.DeviceId, DeviceRole.Phone, d.Kind, d.Name, d.Model,
                d.Online, d.LastSeenAt, d.Status, null, Local: info, AliasKey: KeyOf(d)));
        }
        foreach (var h in overview.Hubs)
        {
            foreach (var d in Freshest(h.Devices))
            {
                if (d.DeviceId == h.SelfDeviceId || !seen.Add(Seen(d.Kind, d.Name))) continue;
                items.Add(new DeviceItem($"via:{h.PcId}:{d.DeviceId}", DeviceRole.ViaHub, d.Kind, d.Name, d.Model,
                    d.Online && h.Online, d.LastSeenAt, d.Status, null, Via: h.PcName, AliasKey: KeyOf(d), ViaKey: GroupKeys.Pc(h.PcId)));
            }
        }
        return items;
    }

    /// <summary>
    /// A linked PC as one entry. Its live view comes the way that answers now: this PC's link to it (a
    /// WebSocket) while that is online or it isn't reporting here either, else it shares its view here. The
    /// key stays the same either way, so the selection and the traces under the gauges survive a switch.
    /// </summary>
    private static DeviceItem PcItem(PcLink link, IReadOnlyDictionary<string, LocalDeviceInfo> local)
    {
        var pc = LinkedPcs.Summarize(link);
        var h = link.Hub;
        var here = link.Here.FirstOrDefault(d => d.Online) ?? link.Here.FirstOrDefault();
        var viaHub = h is not null && (h.Online || here is not { Online: true });
        LocalDeviceInfo? info = null;
        if (here is not null) local.TryGetValue(here.DeviceId, out info);
        // Keyed by pcId once known, so the entry keeps its key when this PC's link to it comes up later.
        return new DeviceItem(
            pc.PcId is { } key ? "hub:" + key : "device:" + here!.DeviceId,
            viaHub ? DeviceRole.Hub : DeviceRole.LinkedHere,
            DeviceKind.Pc, pc.Name, null, pc.Online, pc.LastSeen, pc.Status,
            viaHub ? Core.Mobile.LiveTarget.Hub(h!.PcId) : Core.Mobile.LiveTarget.Device(here!.DeviceId),
            Local: info, Error: h?.Error, Phones: pc.Phones,
            AliasKey: pc.PcId is { } pcId ? GroupKeys.Pc(pcId) : null,
            TwoWay: h is not null && here is not null);
    }

    private static string Seen(string kind, string name) => kind + "|" + name.Trim();

    private static IEnumerable<DeviceEntry> Freshest(IEnumerable<DeviceEntry> devices) =>
        devices.OrderByDescending(d => d.Online).ThenByDescending(d => d.LastSeenAt ?? DateTimeOffset.MinValue);

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
