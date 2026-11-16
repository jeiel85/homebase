using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Monitoring;
using static LocalOpsBot.Tray.Dashboard.Hb;

namespace LocalOpsBot.Tray.Dashboard;

/// <summary>
/// A phone's page (DECISIONS M-46): what it last reported — battery, memory, storage, network and the
/// rest Android tells without extra permissions — plus what only this PC knows (push, pairing date).
/// Phones have no server, so this is their latest report, not a live view.
/// </summary>
internal static class PhoneView
{
    public static FrameworkElement Build(DeviceItem d)
    {
        var root = new StackPanel();
        var s = d.Status;
        if (s is null)
        {
            root.Children.Add(Note(d.Online ? L.DetPhoneNoReportYet : L.DetPhoneNeverReported));
            return root;
        }

        // ── Slab: battery ring + battery facts, then memory and storage meters ──
        var slab = new StackPanel();
        var top = Columns(Px(150), Px(18), Star());
        if (s.Battery is { } b)
        {
            var ring = new Grid { Width = 132, Height = 132 };
            var gauge = new RingGauge { Thickness = 11 };
            gauge.Set(b, Battery(b));
            ring.Children.Add(gauge);
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cap = Label(L.DetBattery, OnSlabSoft);
            cap.HorizontalAlignment = HorizontalAlignment.Center;
            var val = Readout(b + "%", 28);
            val.HorizontalAlignment = HorizontalAlignment.Center;
            var state = Text(s.Charging == true ? L.DetCharging : L.DetOnBattery, 11, s.Charging == true ? LedOn : OnSlabSoft, FontWeights.SemiBold);
            state.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(cap);
            center.Children.Add(val);
            center.Children.Add(state);
            ring.Children.Add(center);
            top.Children.Add(At(ring, 0));
        }
        var facts = new UniformGrid2(2);
        if (s.BatteryTempC is { } t) facts.Add(Fact(L.DetBatteryTemp, Fmt.Temp(t), t >= 45 ? LedOff : t >= 40 ? Accent : null));
        if (s.BatteryHealth is { } h) facts.Add(Fact(L.DetBatteryHealth, L.Health(h), h == "good" ? null : Accent));
        if (s.Charging == true) facts.Add(Fact(L.DetPlug, s.Plug is { } plug ? L.Plug(plug) : L.DetCharging));
        if (s.ChargeFullSec is { } full && s.Charging == true) facts.Add(Fact(L.DetUntilFull, Fmt.Duration(full)));
        if (s.UptimeSec is { } up) facts.Add(Fact(L.DetUptime, Fmt.Uptime(up)));
        if (s.Thermal is { } th) facts.Add(Fact(L.DetThermal, L.Thermal(th), th is "none" or "light" ? null : th == "moderate" ? Accent : LedOff));
        top.Children.Add(At(facts.Root, 2));
        slab.Children.Add(top);

        var meters = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (s.MemUsedBytes is { } mu && s.MemTotalBytes is > 0 and var mt) meters.Children.Add(MeterRow(L.DetMemory, mu, mt));
        if (s.StorageUsedBytes is { } su && s.StorageTotalBytes is > 0 and var st) meters.Children.Add(MeterRow(L.DetStorage, su, st));
        if (meters.Children.Count > 0) slab.Children.Add(meters);
        root.Children.Add(SlabPanel(slab));

        // ── The phone's settings and environment ──
        var info = new InfoGrid();
        info.Add(L.DetNetwork, NetworkText(s));
        if (s.Signal is { } bars) info.Add(L.DetSignal, SignalBars(bars));
        if (s.Os is { } os) info.Add(L.DetSystem, os);
        if (d.Model is { } model) info.Add(L.DetModel, model);
        if (s.PowerSave is { } ps) info.Add(L.DetPowerSave, ps ? L.ChipOnWord : L.ChipOffWord, ps ? Accent : null);
        if (s.Dnd is { } dnd) info.Add(L.DetDnd, dnd ? L.ChipOnWord : L.ChipOffWord, dnd ? Accent : null);
        if (s.Forwarding is { } fw) info.Add(L.DetForwarding, fw ? L.DetForwardingOn : L.DetForwardingOff);
        if (s.AppVersion is { } v) info.Add(L.DetAppVersion, "v" + v);
        root.Children.Add(Section(L.DetPhoneSection, info.Root));

        // ── What this PC knows about the phone ──
        if (d.Role == DeviceRole.Phone)
        {
            var mine = new InfoGrid();
            if (d.Local is { } local)
            {
                mine.Add(L.DetPush, local.Push
                    ? local.Muted is { Count: > 0 } muted ? L.DetPushOnMuted(string.Join(", ", muted.Select(L.CategoryName))) : L.DetPushOn
                    : L.DetPushOff, local.Push ? null : Accent);
                mine.Add(L.DetPairedAt, local.PairedAt.ToLocalTime().ToString("yyyy-MM-dd"));
            }
            mine.Add(L.DetLastReport, d.LastSeen is { } at ? Fmt.Ago(at, DateTimeOffset.UtcNow) : "—");
            root.Children.Add(Section(L.DetWithThisPc, mine.Root));
        }
        else if (d.Via is { } via)
        {
            var seen = new InfoGrid();
            seen.Add(L.DetSeenThrough, via);
            seen.Add(L.DetLastReport, d.LastSeen is { } at ? Fmt.Ago(at, DateTimeOffset.UtcNow) : "—");
            root.Children.Add(Section(L.DetConnection, seen.Root));
        }

        root.Children.Add(Note(s.Os is null ? L.DetPhoneOldApp : L.DetPhoneCadence));
        return root;
    }

    private static string NetworkText(DeviceStatus s)
    {
        var parts = new List<string> { DeviceList.Network(s) ?? L.DetNoNetwork };
        if (s.LinkMbps is { } mbps) parts.Add($"{mbps} Mbps");
        return string.Join(" · ", parts);
    }

    /// <summary>Four rising bars, the filled ones in the signal's colour.</summary>
    private static UIElement SignalBars(int bars)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        var on = bars <= 1 ? LedOff : bars == 2 ? Accent : LedOn;
        for (var i = 0; i < 4; i++)
        {
            p.Children.Add(new Border
            {
                Width = 5,
                Height = 5 + i * 3.5,
                Margin = new Thickness(0, 0, 2, 0),
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = i < bars ? on : Hairline,
            });
        }
        var label = Text($"  {bars}/4", 12, InkSoft);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        p.Children.Add(label);
        return p;
    }

    private static UIElement MeterRow(string label, long used, long total)
    {
        var pct = 100.0 * used / total;
        var row = Columns(Px(70), Star(), Px(150));
        row.Margin = new Thickness(0, 4, 0, 4);
        row.Children.Add(At(Label(label, OnSlabSoft, 11.5), 0));
        var m = new MeterBar();
        m.Set(pct, Load(pct));
        row.Children.Add(At(m, 1));
        var v = Readout($"{Fmt.Gb(used)} / {Fmt.Gb(total)}", 12);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(At(v, 2));
        return row;
    }

    private static UIElement Fact(string label, string value, Brush? tone = null)
    {
        var p = new StackPanel { Margin = new Thickness(0, 4, 12, 8) };
        p.Children.Add(Label(label, OnSlabSoft, 10.5));
        p.Children.Add(Readout(value, 15, tone ?? OnSlab));
        return p;
    }

    internal static UIElement Section(string title, UIElement body)
    {
        var p = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var h = Label(title, InkSoft, 11.5);
        h.Margin = new Thickness(2, 0, 0, 6);
        p.Children.Add(h);
        p.Children.Add(Card(body, new Thickness(16, 8, 16, 8)));
        return p;
    }

    internal static UIElement Note(string text)
    {
        var t = Text(text, 11.5, InkSoft, wrap: true);
        t.Margin = new Thickness(2, 14, 2, 0);
        return t;
    }

    /// <summary>Two columns of label/value rows on a white card.</summary>
    internal sealed class InfoGrid
    {
        private int _row;
        public Grid Root { get; } = Columns(Px(120), Star(), Px(24), Px(120), Star());

        public void Add(string label, string value, Brush? tone = null)
        {
            var v = Text(value, 12.5, tone ?? Ink, FontWeights.SemiBold, wrap: true);
            v.Margin = new Thickness(0, 6, 0, 6);
            Add(label, v);
        }

        public void Add(string label, UIElement value)
        {
            var col = (_row % 2) * 3;
            var r = _row / 2;
            if (col == 0) Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Label(label, InkSoft, 11.5);
            l.Margin = new Thickness(0, 7, 8, 7);
            Root.Children.Add(At(l, col, r));
            Root.Children.Add(At(value, col + 1, r));
            _row++;
        }
    }

    /// <summary>A wrap of fixed-width cells (facts beside the battery ring).</summary>
    private sealed class UniformGrid2
    {
        private readonly int _columns;
        private int _n;

        public UniformGrid2(int columns)
        {
            _columns = columns;
            for (var i = 0; i < columns; i++) Root.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
        }

        public Grid Root { get; } = new() { VerticalAlignment = VerticalAlignment.Center };

        public void Add(UIElement e)
        {
            if (_n % _columns == 0) Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.Children.Add(At(e, _n % _columns, _n / _columns));
            _n++;
        }
    }
}

/// <summary>
/// A PC's page when no live view is possible: seen through another PC, too old to share, offline, or
/// this PC while the phone connection is off. Shows its last reported CPU, memory and uptime.
/// </summary>
internal static class PcSummaryView
{
    public static FrameworkElement Build(DeviceItem d, string? reason)
    {
        var root = new StackPanel();
        var s = d.Status;
        var slab = new StackPanel();
        var row = Columns(Star(), Px(14), Star());
        row.Children.Add(At(Ring("CPU", s?.CpuPct, s?.CpuPct is { } c ? Fmt.Pct(c) : "—", null), 0));
        double? mem = s?.MemUsedBytes is { } u && s.MemTotalBytes is > 0 and var t ? 100.0 * u / t : null;
        row.Children.Add(At(Ring(L.DetMemory, mem, mem is { } m ? Fmt.Pct(m) : "—",
            s?.MemUsedBytes is { } used && s.MemTotalBytes is { } total ? $"{Fmt.Gb(used)} / {Fmt.Gb(total)}" : null), 2));
        slab.Children.Add(row);
        var foot = new List<string>();
        if (s?.UptimeSec is { } up) foot.Add(L.DetUptime + " " + Fmt.Uptime(up));
        if (s?.Os is { } os) foot.Add(os);
        if (foot.Count > 0)
        {
            var f = Text(string.Join("  ·  ", foot), 11.5, OnSlabSoft);
            f.Margin = new Thickness(0, 12, 0, 0);
            f.HorizontalAlignment = HorizontalAlignment.Center;
            slab.Children.Add(f);
        }
        root.Children.Add(SlabPanel(slab));
        if (reason is not null) root.Children.Add(PhoneView.Note(reason));
        return root;
    }

    /// <summary>This PC from the Tray's own collectors, when there is no bridge to the Agent.</summary>
    public static FrameworkElement BuildLocal(SystemMetricSnapshot? m, IReadOnlyList<DiskSnapshot> disks, NetworkStatusSnapshot? net, string reason)
    {
        var root = new StackPanel();
        var slab = new StackPanel();
        var row = Columns(Star(), Px(14), Star());
        var cpu = m?.CpuUsagePercent;
        row.Children.Add(At(Ring("CPU", cpu, cpu is { } c ? Fmt.Pct(c) : "—", null), 0));
        double? mem = null;
        string? memSub = null;
        if (m?.TotalMemoryBytes is > 0 and var total && m.AvailableMemoryBytes is { } avail)
        {
            mem = m.MemoryUsagePercent ?? 100.0 * (total - avail) / total;
            memSub = $"{Fmt.Gb(total - avail)} / {Fmt.Gb(total)}";
        }
        row.Children.Add(At(Ring(L.DetMemory, mem, mem is { } x ? Fmt.Pct(x) : "—", memSub), 2));
        slab.Children.Add(row);
        var drives = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var d in disks.Where(d => d.IsReady && d.TotalBytes > 0))
        {
            var pct = 100.0 * (d.TotalBytes - d.FreeBytes) / d.TotalBytes;
            var r = Columns(Px(34), Star(), Px(170));
            r.Margin = new Thickness(0, 3, 0, 3);
            r.Children.Add(At(Label(d.Name.TrimEnd('\\'), OnSlabSoft, 12), 0));
            var meter = new MeterBar();
            meter.Set(pct, Load(pct));
            r.Children.Add(At(meter, 1));
            var free = Readout(L.DetFreeOf(Fmt.Gb(d.FreeBytes), Fmt.Gb(d.TotalBytes)), 12);
            free.HorizontalAlignment = HorizontalAlignment.Right;
            r.Children.Add(At(free, 2));
            drives.Children.Add(r);
        }
        if (drives.Children.Count > 0) slab.Children.Add(drives);
        var foot = new List<string>();
        if (m is not null) foot.Add(L.DetUptime + " " + Fmt.Uptime((long)m.Uptime.TotalSeconds));
        if (net is not null) foot.Add(net.IsOnline ? L.Online(net.PrimaryIPv4 ?? L.NoIp) : L.Offline);
        if (foot.Count > 0)
        {
            var f = Text(string.Join("  ·  ", foot), 11.5, OnSlabSoft);
            f.Margin = new Thickness(0, 10, 0, 0);
            slab.Children.Add(f);
        }
        root.Children.Add(SlabPanel(slab));
        root.Children.Add(PhoneView.Note(reason));
        return root;
    }

    private static UIElement Ring(string caption, double? pct, string value, string? sub)
    {
        var p = new StackPanel();
        var ring = new Grid { Width = 116, Height = 116, HorizontalAlignment = HorizontalAlignment.Center };
        var g = new RingGauge { Thickness = 10 };
        g.Set(pct ?? 0, Load(pct ?? 0));
        ring.Children.Add(g);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var cap = Label(caption, OnSlabSoft);
        cap.HorizontalAlignment = HorizontalAlignment.Center;
        var val = Readout(value, 24);
        val.HorizontalAlignment = HorizontalAlignment.Center;
        center.Children.Add(cap);
        center.Children.Add(val);
        ring.Children.Add(center);
        p.Children.Add(ring);
        if (sub is not null)
        {
            var t = Text(sub, 11, OnSlabSoft);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            p.Children.Add(t);
        }
        return p;
    }
}
