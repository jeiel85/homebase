using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LocalOpsBot.Core.Mobile;
using static LocalOpsBot.Tray.Dashboard.Hb;

namespace LocalOpsBot.Tray.Dashboard;

/// <summary>
/// A PC's page, live (DECISIONS M-44) — the same reading the phone app shows: CPU, memory and main GPU
/// gauges with 60-second traces, disk I/O, network, ping and temperature cells, drives, and the apps,
/// processes and work sessions. Built once per device and updated in place every second.
/// </summary>
internal sealed class PcLiveView
{
    private enum Tab { Apps, Processes, Work }
    private enum Sort { Cpu, Memory, Name }

    private readonly History _history;
    private readonly StackPanel _root = new();
    private readonly Gauge _cpu, _mem, _gpu;
    private readonly Grid _gauges;
    private readonly RateCell _io, _rx, _tx, _ping, _temp;
    private readonly Grid _cells;
    private readonly StackPanel _drives = new();
    private readonly StackPanel _gpuLines = new();
    private readonly TextBlock _footer = Text("", 11.5, OnSlabSoft);
    private readonly TextBlock _notice = Text("", 12, Ink, wrap: true);
    private readonly Border _noticeBox;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _sorts = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new();
    private readonly HashSet<int> _expanded = new();
    private readonly StableOrder _appOrder = new(), _processOrder = new();
    /// <summary>Design preview only (<c>--tab</c>): the tab a new page opens on (0 apps, 1 processes, 2 work).</summary>
    internal static int PreviewTab;

    private Tab _tab = (Tab)Math.Clamp(PreviewTab, 0, 2);
    private Sort _sort = Sort.Cpu;
    private LiveSnapshot? _last;

    public PcLiveView(History history)
    {
        _history = history;
        _noticeBox = new Border
        {
            Background = WarnField,
            BorderBrush = Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = _notice,
            Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_noticeBox);

        _cpu = new Gauge("CPU");
        _mem = new Gauge(L.DetMemory);
        _gpu = new Gauge("GPU");
        _gauges = Columns(Star(), Px(14), Star(), Px(14), Star());
        _gauges.Children.Add(At(_cpu.Root, 0));
        _gauges.Children.Add(At(_mem.Root, 2));
        _gauges.Children.Add(At(_gpu.Root, 4));

        _io = new RateCell(L.DetDiskIo, MeterOk);
        _rx = new RateCell(L.DetReceive, Teal);
        _tx = new RateCell(L.DetSend, Accent);
        _ping = new RateCell(L.DetPing, MeterOk);
        _temp = new RateCell(L.DetTemp, Accent);
        _cells = Columns(Star(), Px(8), Star(), Px(8), Star(), Px(8), Star(), Px(8), Star());
        _cells.Margin = new Thickness(0, 16, 0, 0);
        _cells.Children.Add(At(_io.Root, 0));
        _cells.Children.Add(At(_rx.Root, 2));
        _cells.Children.Add(At(_tx.Root, 4));
        _cells.Children.Add(At(_ping.Root, 6));
        _cells.Children.Add(At(_temp.Root, 8));

        var slab = new StackPanel();
        slab.Children.Add(_gauges);
        slab.Children.Add(_cells);
        _drives.Margin = new Thickness(0, 14, 0, 0);
        slab.Children.Add(_drives);
        _gpuLines.Margin = new Thickness(0, 10, 0, 0);
        slab.Children.Add(_gpuLines);
        _footer.Margin = new Thickness(0, 10, 0, 0);
        slab.Children.Add(_footer);
        _root.Children.Add(SlabPanel(slab));

        var bar = Columns(Star(), Auto);
        bar.Margin = new Thickness(0, 16, 0, 8);
        bar.Children.Add(At(_tabs, 0));
        bar.Children.Add(At(_sorts, 1));
        _root.Children.Add(bar);
        _root.Children.Add(Card(_list, new Thickness(0, 4, 0, 4)));
    }

    public FrameworkElement Root => _root;

    /// <summary>A note above the panel (the view is stale, or reconnecting); null hides it.</summary>
    public void Notice(string? text)
    {
        _notice.Text = text ?? "";
        _noticeBox.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Update(LiveSnapshot s)
    {
        _last = s;
        _history.Add(s);
        var sys = s.System;
        var memPct = sys.MemTotalBytes > 0 ? 100.0 * sys.MemUsedBytes / sys.MemTotalBytes : 0;
        _cpu.Set(sys.CpuPct, Fmt.Pct(sys.CpuPct), s.Hardware?.CpuName is { } cpuName ? Short(cpuName) : null, _history.Cpu);
        _mem.Set(memPct, Fmt.Pct(memPct), $"{Fmt.Gb(sys.MemUsedBytes)} / {Fmt.Gb(sys.MemTotalBytes)}", _history.Mem);

        var hw = s.Hardware;
        var gpu = hw?.Gpus.FirstOrDefault();
        if (gpu?.LoadPct is { } load)
        {
            _gpu.Root.Visibility = Visibility.Visible;
            _gpu.Set(load, Fmt.Pct(load), Short(gpu.Name), _history.Gpu);
        }
        else _gpu.Root.Visibility = Visibility.Hidden;

        _io.Set(Fmt.Rate(sys.IoBps), _history.Io);
        _rx.Set(Fmt.Rate(sys.NetRxBps), _history.Rx);
        _tx.Set(Fmt.Rate(sys.NetTxBps), _history.Tx);
        if (hw?.Ping is { } ping)
        {
            _ping.Root.Visibility = Visibility.Visible;
            var loss = ping.LossPct > 0 ? $" · {L.DetLoss} {ping.LossPct:0}%" : "";
            _ping.Set(ping.LastMs is { } ms ? Fmt.Ms(ms) : "—", _history.Ping,
                (ping.AvgMs is { } avg ? $"{L.DetAverage} {Fmt.Ms(avg)}" : ping.Host) + loss);
        }
        else _ping.Root.Visibility = Visibility.Hidden;
        if ((hw?.CpuTempC ?? hw?.BoardTempC) is { } t)
        {
            _temp.Root.Visibility = Visibility.Visible;
            _temp.Set(Fmt.Temp(t), _history.Temp, hw?.CpuTempC is null ? L.DetBoard : "CPU");
        }
        else _temp.Root.Visibility = Visibility.Hidden;

        UpdateDrives(hw?.Disks ?? Array.Empty<DiskUsage>());
        UpdateGpuLines(hw?.Gpus ?? Array.Empty<GpuReading>());
        var footer = new List<string> { L.DetUptime + " " + Fmt.Uptime(sys.UptimeSec) };
        if (hw?.CpuName is { } name) footer.Add(name.Trim());
        if (!s.NetPerProcess) footer.Add(L.DetNoNetPerProcess);
        _footer.Text = string.Join("  ·  ", footer);

        RenderTabs(s);
        RenderList(s);
    }

    /// <summary>"AMD Ryzen 7 250 w/ Radeon 780M Graphics" → "AMD Ryzen 7 250"; "NVIDIA GeForce RTX 5060 …" → "RTX 5060 …".</summary>
    internal static string Short(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name, @"\s+(w/|with)\s.*$", "");
        s = s.Replace("NVIDIA GeForce ", "", StringComparison.Ordinal)
            .Replace("AMD Radeon ", "Radeon ", StringComparison.Ordinal)
            .Replace("(R)", "", StringComparison.Ordinal)
            .Replace("(TM)", "", StringComparison.Ordinal)
            .Replace(" CPU", "", StringComparison.Ordinal);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+@.*$", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    private void UpdateDrives(IReadOnlyList<DiskUsage> disks)
    {
        _drives.Children.Clear();
        foreach (var d in disks.Where(d => d.TotalBytes > 0))
        {
            var pct = 100.0 * d.UsedBytes / d.TotalBytes;
            var row = Columns(Px(34), Star(), Px(170));
            row.Margin = new Thickness(0, 3, 0, 3);
            row.Children.Add(At(Label(d.Name, OnSlabSoft, 12), 0));
            var meter = new MeterBar();
            meter.Set(pct, Load(pct));
            row.Children.Add(At(meter, 1));
            var free = Readout(L.DetFreeOf(Fmt.Gb(d.TotalBytes - d.UsedBytes), Fmt.Gb(d.TotalBytes)), 12, OnSlab);
            free.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(At(free, 2));
            _drives.Children.Add(row);
        }
        _drives.Visibility = _drives.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateGpuLines(IReadOnlyList<GpuReading> gpus)
    {
        _gpuLines.Children.Clear();
        foreach (var g in gpus.Take(3))
        {
            var parts = new List<string> { Short(g.Name) };
            if (g.LoadPct is { } l) parts.Add(Fmt.Pct(l));
            if (g.VramUsedBytes is { } u && g.VramTotalBytes is > 0 and var tot && !g.Integrated) parts.Add($"VRAM {Fmt.Gb(u)} / {Fmt.Gb(tot)}");
            else if (g.SharedUsedBytes is { } su && g.Integrated) parts.Add($"{L.DetShared} {Fmt.Gb(su)}");
            if (g.TempC is { } t) parts.Add(Fmt.Temp(t));
            if (g.PowerW is { } w) parts.Add($"{w:0} W");
            else if (g.PowerPct is { } pp) parts.Add($"{L.DetPower} {pp:0}%");
            if (g.ClockMhz is { } c) parts.Add($"{c:0} MHz");
            if (g.FanPct is { } f) parts.Add($"{L.DetFan} {f:0}%");
            _gpuLines.Children.Add(Text(string.Join("  ·  ", parts), 11.5, OnSlabSoft));
        }
        _gpuLines.Visibility = _gpuLines.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Apps / processes / work ──

    private void RenderTabs(LiveSnapshot s)
    {
        _tabs.Children.Clear();
        AddTab(Tab.Apps, L.DetApps, s.SessionAvailable ? s.Apps.Count.ToString() : "—");
        AddTab(Tab.Processes, L.DetProcesses, s.Processes.Count.ToString());
        AddTab(Tab.Work, L.DetWork, Work(s).Count.ToString());
        _sorts.Children.Clear();
        if (_tab == Tab.Work) return;
        _sorts.Children.Add(Label(L.DetSortBy + " ", InkSoft, 11));
        AddSort(Sort.Cpu, "CPU");
        AddSort(Sort.Memory, L.DetMemory);
        if (_tab == Tab.Apps) AddSort(Sort.Name, L.DetName);
    }

    private void AddTab(Tab tab, string label, string count)
    {
        var on = tab == _tab;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Text(label, 12.5, on ? OnSlab : Ink, FontWeights.SemiBold));
        content.Children.Add(Text("  " + count, 11.5, on ? Accent : InkSoft, FontWeights.SemiBold));
        var b = new Border
        {
            Background = on ? Slab : Plate,
            BorderBrush = on ? Accent : Hairline,
            BorderThickness = on ? new Thickness(0, 0, 0, 3) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Child = content,
        };
        b.MouseLeftButtonUp += (_, _) => { _tab = tab; if (_last is { } s) { RenderTabs(s); RenderList(s); } };
        _tabs.Children.Add(b);
    }

    private void AddSort(Sort sort, string label)
    {
        var on = sort == _sort;
        var t = Text(label, 11.5, on ? Indigo : InkSoft, on ? FontWeights.Bold : FontWeights.Normal);
        t.Margin = new Thickness(6, 0, 0, 0);
        t.Cursor = Cursors.Hand;
        t.TextDecorations = on ? TextDecorations.Underline : null;
        t.MouseLeftButtonUp += (_, _) => { _sort = sort; _appOrder.Reset(); _processOrder.Reset(); if (_last is { } s) { RenderTabs(s); RenderList(s); } };
        _sorts.Children.Add(t);
    }

    private static IReadOnlyList<WorkItem> Work(LiveSnapshot s) =>
        s.Work ?? s.ClaudeSessions.Select(c => c.ToWork()).ToList();

    private void RenderList(LiveSnapshot s)
    {
        _list.Children.Clear();
        switch (_tab)
        {
            case Tab.Apps:
                if (!s.SessionAvailable) { Empty(L.DetNoSession); return; }
                _list.Children.Add(Header());
                var apps = _sort == Sort.Name
                    ? s.Apps.OrderBy(a => a.Process, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Pid)
                    : _appOrder.Arrange(s.Apps, a => a.Pid, a => Score(a.Usage));
                foreach (var a in apps) _list.Children.Add(AppRow(a, s.NetPerProcess));
                if (s.Apps.Count == 0) Empty(L.DetNoApps);
                break;
            case Tab.Processes:
                _list.Children.Add(Header());
                foreach (var p in _processOrder.Arrange(s.Processes, p => p.Pid, p => Score(p.Usage)))
                    _list.Children.Add(UsageRow(p.Name, $"PID {p.Pid}", p.Usage, s.NetPerProcess, null));
                break;
            default:
                var work = Work(s);
                foreach (var w in work) _list.Children.Add(WorkRow(w, s.Ts));
                if (work.Count == 0) Empty(L.DetNoWork);
                break;
        }
    }

    private double Score(ResourceUsage u) => _sort == Sort.Memory ? u.MemBytes : u.CpuPct;

    private void Empty(string text)
    {
        var t = Text(text, 12, InkSoft, wrap: true);
        t.Margin = new Thickness(16, 14, 16, 14);
        _list.Children.Add(t);
    }

    private static readonly GridLength[] UsageColumns = { new(1, GridUnitType.Star), new(62), new(78), new(78), new(112) };

    private static Grid Header()
    {
        var g = Columns(UsageColumns);
        g.Margin = new Thickness(16, 6, 16, 6);
        g.Children.Add(At(Label(L.DetName, InkSoft, 10.5), 0));
        string[] heads = { "CPU", L.DetMemory, L.DetDisk, L.DetNetwork };
        for (var i = 0; i < heads.Length; i++)
        {
            var h = Label(heads[i], InkSoft, 10.5);
            h.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(At(h, i + 1));
        }
        return g;
    }

    private UIElement AppRow(AppEntry a, bool netPerProcess)
    {
        var fg = a.Windows.FirstOrDefault(w => w.Foreground) ?? a.Windows.FirstOrDefault();
        var expanded = _expanded.Contains(a.Pid);
        var extra = expanded ? a.Windows.Where(w => w != fg).Take(8).Select(w => "· " + w.Title).ToList() : null;
        if (expanded && a.Windows.Count > 9) extra!.Add(L.DetMoreWindows(a.Windows.Count - 9));
        if (expanded) foreach (var c in a.Children.Take(5)) extra!.Add($"↳ {c.Name}  {Fmt.Pct(c.Usage.CpuPct)} · {Fmt.Bytes(c.Usage.MemBytes)}");
        var row = UsageRow(a.Process, fg?.Title ?? "", a.Usage, netPerProcess, extra, a.Foreground);
        if (a.Windows.Count > 1 || a.Children.Count > 0)
        {
            row.Cursor = Cursors.Hand;
            row.ToolTip = expanded ? null : L.DetExpandHint;
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (!_expanded.Remove(a.Pid)) _expanded.Add(a.Pid);
                if (_last is { } s) RenderList(s);
            };
        }
        return row;
    }

    private static Border UsageRow(string name, string sub, ResourceUsage u, bool netPerProcess, IReadOnlyList<string>? extra, bool foreground = false)
    {
        var g = Columns(UsageColumns);
        var left = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        if (foreground)
        {
            var dot = Led(Accent, 7);
            dot.Margin = new Thickness(0, 0, 6, 0);
            dot.ToolTip = L.DetForeground;
            title.Children.Add(dot);
        }
        title.Children.Add(Text(name, 12.5, Ink, FontWeights.SemiBold));
        left.Children.Add(title);
        if (sub.Length > 0) left.Children.Add(Text(sub, 11.5, InkSoft));
        if (extra is not null) foreach (var e in extra) left.Children.Add(Text(e, 11, InkSoft));
        g.Children.Add(At(left, 0));
        void Num(int col, string text, Brush? fg = null)
        {
            var t = Text(text, 12, fg ?? Ink);
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            t.VerticalAlignment = VerticalAlignment.Top;
            t.Margin = new Thickness(0, 1, 0, 0);
            g.Children.Add(At(t, col));
        }
        Num(1, Fmt.Pct(u.CpuPct), u.CpuPct >= 50 ? Danger : u.CpuPct >= 20 ? Indigo : Ink);
        Num(2, Fmt.Bytes(u.MemBytes));
        Num(3, u.IoBps > 0 ? Fmt.Rate(u.IoBps) : "—", InkSoft);
        Num(4, !netPerProcess || u.NetRxBps is null ? "—" : $"↓{Fmt.Rate(u.NetRxBps.Value)} ↑{Fmt.Rate(u.NetTxBps ?? 0)}", InkSoft);
        var row = new Border
        {
            Padding = new Thickness(16, 7, 16, 7),
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Brushes.Transparent,
            Child = g,
        };
        row.MouseEnter += (_, _) => row.Background = PlateHover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private static Border WorkRow(WorkItem w, DateTimeOffset now)
    {
        var (kind, color) = w.Kind switch
        {
            WorkKind.Agent => (L.WorkAgent, Indigo),
            WorkKind.Editor => (L.WorkEditor, B2(0x20, 0x64, 0x79)),
            WorkKind.Document => (L.WorkDocument, B2(0x8A, 0x5A, 0x00)),
            _ => (L.WorkBrowser, B2(0x5A, 0x5F, 0x8C)),
        };
        var g = Columns(Px(92), Star());
        var pill = Pill(kind, color, OnSlab);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        pill.VerticalAlignment = VerticalAlignment.Top;
        pill.Margin = new Thickness(0, 1, 0, 0);
        g.Children.Add(At(pill, 0));
        var text = new StackPanel();
        text.Children.Add(Text(w.Title, 12.5, Ink, FontWeights.SemiBold));
        var meta = new List<string> { w.App };
        if (!string.IsNullOrEmpty(w.Detail)) meta.Add(w.Detail!);
        if (w.LastActivity is { } at) meta.Add(Fmt.Ago(at, now));
        if (w.Foreground) meta.Add(L.DetForeground);
        text.Children.Add(Text(string.Join(" · ", meta), 11.5, InkSoft));
        g.Children.Add(At(text, 1));
        return new Border { Padding = new Thickness(16, 8, 16, 8), BorderBrush = Hairline, BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
    }

    private static Brush B2(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    /// <summary>
    /// Keeps a list's order steady: items are ranked by their average over the last ranking period and
    /// re-ranked every 5 seconds, so rows don't swap places every second (the phone's AppOrder idea).
    /// </summary>
    private sealed class StableOrder
    {
        private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);
        private readonly Dictionary<int, double> _sum = new();
        private readonly Dictionary<int, int> _n = new();
        private List<int> _order = new();
        private DateTime _rankedAt;

        public void Reset() => _rankedAt = DateTime.MinValue;

        public IEnumerable<T> Arrange<T>(IReadOnlyList<T> items, Func<T, int> key, Func<T, double> score)
        {
            foreach (var i in items)
            {
                var k = key(i);
                _sum[k] = _sum.GetValueOrDefault(k) + score(i);
                _n[k] = _n.GetValueOrDefault(k) + 1;
            }
            var byKey = new Dictionary<int, T>();
            foreach (var i in items) byKey[key(i)] = i;
            if (DateTime.UtcNow - _rankedAt >= Period || _order.Count == 0)
            {
                _order = byKey.Keys.OrderByDescending(k => _sum[k] / _n[k]).ThenBy(k => k).ToList();
                _sum.Clear();
                _n.Clear();
                _rankedAt = DateTime.UtcNow;
            }
            var placed = new HashSet<int>();
            foreach (var k in _order)
                if (byKey.TryGetValue(k, out var item) && placed.Add(k)) yield return item;
            foreach (var (k, item) in byKey)
                if (placed.Add(k)) yield return item;
        }
    }

    /// <summary>A ring gauge with its value, a caption, a sub-line and the trace under it.</summary>
    private sealed class Gauge
    {
        private readonly RingGauge _ring = new() { Width = 116, Height = 116, Thickness = 10 };
        private readonly TextBlock _value = Readout("—", 24);
        private readonly TextBlock _sub = Text("", 11, OnSlabSoft);
        private readonly TraceView _trace = new() { Height = 26, Max = 100, Margin = new Thickness(0, 8, 0, 0) };

        public Gauge(string caption)
        {
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cap = Label(caption, OnSlabSoft, 11);
            cap.HorizontalAlignment = HorizontalAlignment.Center;
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(cap);
            center.Children.Add(_value);
            var ring = new Grid { Width = 116, Height = 116, HorizontalAlignment = HorizontalAlignment.Center };
            ring.Children.Add(_ring);
            ring.Children.Add(center);
            Root.Children.Add(ring);
            _sub.HorizontalAlignment = HorizontalAlignment.Center;
            _sub.Margin = new Thickness(0, -6, 0, 0);
            Root.Children.Add(_sub);
            Root.Children.Add(_trace);
        }

        public StackPanel Root { get; } = new();

        public void Set(double pct, string value, string? sub, IReadOnlyList<double> trace)
        {
            var brush = Load(pct);
            _ring.Set(pct, brush);
            _value.Text = value;
            _sub.Text = sub ?? "";
            _trace.Stroke = brush == MeterOk ? MeterOk : brush;
            _trace.SetValues(trace);
        }
    }

    /// <summary>A raised cell on the slab: caption, readout, optional note and its trace.</summary>
    private sealed class RateCell
    {
        private readonly TextBlock _value = Readout("—", 14);
        private readonly TextBlock _note = Text("", 10.5, OnSlabSoft);
        private readonly TraceView _trace;

        public RateCell(string caption, Brush color)
        {
            _trace = new TraceView { Height = 24, Stroke = color, Margin = new Thickness(0, 6, 0, 0) };
            var p = new StackPanel();
            p.Children.Add(Label(caption, OnSlabSoft, 10.5));
            p.Children.Add(_value);
            p.Children.Add(_note);
            p.Children.Add(_trace);
            Root = Cell(p);
        }

        public Border Root { get; }

        public void Set(string value, IReadOnlyList<double> trace, string? note = null)
        {
            _value.Text = value;
            _note.Text = note ?? "";
            _note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
            _trace.SetValues(trace);
        }
    }
}
