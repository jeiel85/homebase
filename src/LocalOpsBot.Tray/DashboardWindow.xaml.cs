using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Windows;
using LocalOpsBot.Tray.Dashboard;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;
using static LocalOpsBot.Tray.Dashboard.Hb;

namespace LocalOpsBot.Tray;

/// <summary>The Homebase window's pages, picked in its masthead (DECISIONS M-49).</summary>
public enum DashTab { Devices, Notifications, Settings }

/// <summary>
/// The Homebase window, opened from the tray. Three pages picked in the masthead (DECISIONS M-49):
/// <list type="bullet">
/// <item>Devices, a device hub (M-46): every device on the left — this PC, the PCs linked either way,
/// the phones — and the selected one on the right at the level the phone app shows a PC. PCs are live
/// (this PC from the Agent's own sampler, a linked PC over its live view); phones show their last
/// report. Read-only by design.</item>
/// <item>The notification center (<see cref="NotificationCenterPanel"/>, M-32).</item>
/// <item>Settings (<see cref="SettingsPanel"/>).</item>
/// </list>
/// Nothing is collected while a page is out of view: the Agent closes a live view nobody asked about
/// for 10 s, and the other pages read only when they come into view.
/// </summary>
public partial class DashboardWindow : ThemedWindow
{
    private const string ServiceName = "Homebase.Agent";

    private readonly IDashboardSource _source;
    private readonly bool _preview;
    private readonly ISystemMetricsCollector _metrics = new WindowsSystemMetricsCollector();
    private readonly IDiskCollector _disk = new WindowsDiskCollector();
    private readonly INetworkStatusChecker _network = new WindowsNetworkStatusChecker();
    private readonly ConnectionReadiness _readiness = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, History> _histories = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceItem> _items = Array.Empty<DeviceItem>();
    private string _selected = DeviceList.SelfKey;
    private PcLiveView? _live;
    private string? _liveKey;
    private string? _shownKey;
    private bool _refreshing;
    private int _ticks;
    private Connections? _connections;
    private DashTab _tab = DashTab.Devices;

    internal DashboardWindow(UpdateCoordinator updates) : this(new BridgeDashboardSource(), preview: false, updates) { }

    internal DashboardWindow(IDashboardSource source, bool preview, UpdateCoordinator updates)
    {
        _source = source;
        _preview = preview;
        InitializeComponent();
        SettingsView.Attach(updates);
        if (!preview)
        {
            DashboardPrefs.Load(this, out var selected);
            if (selected is not null) _selected = selected;
        }

        NavNotifications.Content = L.NavNotifications(NotificationCenter.Unread);
        Action<int> onUnread = n => NavNotifications.Content = L.NavNotifications(n);
        NotificationCenter.UnreadChanged += onUnread;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();

        // Refresh at once whenever the window is shown again and stop asking for live views while it
        // is hidden. Hiding is what closing the old Settings window was: unsaved input there is dropped.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _ticks = 0; Refresh(); _timer.Start(); }
            else { _timer.Stop(); if (!_preview) DashboardPrefs.Save(this, _selected); }
            SyncPages();
            if (!IsVisible) SettingsView.Discard();
        };
        // Minimized is out of view too: the Settings page stops polling the Agent (M-13).
        StateChanged += (_, _) => SyncPages();
        Closed += (_, _) =>
        {
            _timer.Stop();
            NotificationCenter.UnreadChanged -= onUnread;
            CenterView.Stop();
            SettingsView.Stop();
        };
    }

    // ── Pages ──

    /// <summary>The page in front, as the tray needs to know whether a new notification is being looked at.</summary>
    internal bool ShowsNotifications => CenterView.InView;

    /// <summary>Brings a page to the front (and, for Settings, one of its areas). The window is not shown here.</summary>
    public void ShowTab(DashTab tab, SettingsTab? area = null)
    {
        if (area is { } a) SettingsView.ShowTab(a);
        // Checking the tab raises Nav_Checked, which switches the page.
        (tab switch
        {
            DashTab.Notifications => NavNotifications,
            DashTab.Settings => NavSettings,
            _ => NavDevices,
        }).IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (CenterView is null) return; // raised while the XAML is still loading
        var tab = ReferenceEquals(sender, NavNotifications) ? DashTab.Notifications
            : ReferenceEquals(sender, NavSettings) ? DashTab.Settings
            : DashTab.Devices;
        if (tab == _tab) return;
        _tab = tab;
        DevicesView.Visibility = tab == DashTab.Devices ? Visibility.Visible : Visibility.Collapsed;
        CenterView.Visibility = tab == DashTab.Notifications ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tab == DashTab.Settings ? Visibility.Visible : Visibility.Collapsed;
        SyncPages();
        // Back on Devices: look again at once (a setting may just have changed), chips included.
        if (tab == DashTab.Devices && IsVisible)
        {
            _ticks = 0;
            Refresh();
        }
    }

    /// <summary>Tells the Notifications and Settings pages whether they are in view now.</summary>
    private void SyncPages()
    {
        var shown = IsVisible && WindowState != WindowState.Minimized;
        CenterView.SetInView(shown && _tab == DashTab.Notifications);
        SettingsView.SetInView(shown && _tab == DashTab.Settings);
    }

    private async void Refresh()
    {
        // Ticks re-enter if a request runs long; skip overlapping refreshes (UI thread only, no lock).
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (_tab != DashTab.Devices)
            {
                // Another page is in front: keep the service badge current, ask for no device data.
                if (_ticks % 4 == 0)
                    ApplyServiceStatus(_preview ? new ServiceStatusInfo("Running", true, true) : await Task.Run(ReadServiceStatus));
                return;
            }
            if (_ticks % 4 == 0)
            {
                ApplyServiceStatus(_preview ? new ServiceStatusInfo("Running", true, true) : await Task.Run(ReadServiceStatus));
                var overview = _source.Connected ? await _source.OverviewAsync() : null;
                // A failed answer keeps what is shown; no bridge at all leaves just this PC.
                if (overview is not null || !_source.Connected || _items.Count == 0)
                {
                    _items = DeviceList.Build(overview, Environment.MachineName);
                    if (_items.All(i => i.Key != _selected)) _selected = DeviceList.SelfKey;
                    RenderRail();
                }
            }
            if (_ticks % 30 == 0 && !_preview)
            {
                var readiness = await Task.Run(_readiness.Probe);
                var forwarding = await Task.Run(SafeForwardingEnabled);
                var autostartOff = await Task.Run(TrayAutostart.IsDisabled);
                _connections = new Connections(readiness, forwarding, _source.FeatureEnabled, autostartOff);
            }
            else if (_preview && _connections is null)
            {
                _connections = new Connections(new ReadinessSnapshot(AgentServiceState.Running, true, true, 1), true, true, false);
            }
            await RenderDetailAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A transient failure (bridge dropped mid-request, WMI hiccup): keep what is shown.
            TrayLog.Info("dashboard", $"Refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _ticks++;
            _refreshing = false;
        }
    }

    /// <summary>Design preview only: open on a given device (<c>--select</c>).</summary>
    internal void PreviewSelect(string key) => _selected = key;

    private DeviceItem Selected() => _items.FirstOrDefault(i => i.Key == _selected) ?? _items[0];

    // ── Device list ──

    private void RenderRail()
    {
        RailPanel.Children.Clear();
        string? group = null;
        foreach (var d in _items)
        {
            var g = d.Role == DeviceRole.Self ? L.RailThisPc : d.IsPc ? L.RailPcs : L.RailPhones;
            if (g != group)
            {
                group = g;
                var head = Label(g, InkSoft, 11);
                head.Margin = new Thickness(6, RailPanel.Children.Count == 0 ? 0 : 12, 0, 6);
                RailPanel.Children.Add(head);
            }
            RailPanel.Children.Add(Tile(d));
        }
        if (_items.Count == 1)
        {
            var hint = Text(_source.FeatureEnabled ? L.DevNone : L.DevFeatureOff, 11.5, InkSoft, wrap: true);
            hint.Margin = new Thickness(6, 14, 6, 0);
            RailPanel.Children.Add(hint);
        }
    }

    private Border Tile(DeviceItem d)
    {
        var on = d.Key == _selected;
        var g = Columns(Px(30), Star(), Auto);
        var glyph = Glyph(d.IsPc, on ? OnSlab : Ink, 20);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 2, 0, 0);
        g.Children.Add(At(glyph, 0));
        var text = new StackPanel();
        text.Children.Add(Text(d.Name, 13, on ? OnSlab : Ink, FontWeights.SemiBold));
        text.Children.Add(Text(DeviceList.Summary(d), 11.5, on ? OnSlabSoft : InkSoft));
        if (RoleLine(d) is { } role) text.Children.Add(Text(role, 10.5, on ? Accent : InkSoft));
        g.Children.Add(At(text, 1));
        var led = Led(d.Online ? LedOn : Idle, 8);
        led.VerticalAlignment = VerticalAlignment.Top;
        led.Margin = new Thickness(8, 5, 0, 0);
        g.Children.Add(At(led, 2));
        var tile = new Border
        {
            Background = on ? Slab : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            Child = g,
        };
        if (!on)
        {
            tile.MouseEnter += (_, _) => tile.Background = Plate;
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        }
        tile.MouseLeftButtonUp += (_, _) => Select(d.Key);
        return tile;
    }

    private static string? RoleLine(DeviceItem d) => d.Role switch
    {
        DeviceRole.Hub => L.RoleHub,
        DeviceRole.LinkedHere => L.RoleLinkedHere,
        DeviceRole.ViaHub => L.DevVia(d.Via ?? ""),
        _ => null,
    };

    private void Select(string key)
    {
        if (key == _selected) return;
        _selected = key;
        _shownKey = null;
        RenderRail();
        DetailScroll.ScrollToTop();
        _ = RenderDetailAsync();
    }

    // ── Selected device ──

    private async Task RenderDetailAsync()
    {
        if (_items.Count == 0) return;
        var d = Selected();
        RenderHeader(d);
        var key = d.Key;

        // An Agent too old for live views is asked again now and then: the service may have been updated since.
        if (d.LiveTarget is { } target && _source.Connected && (!_source.LiveUnsupported || _ticks % 30 == 0))
        {
            var view = await _source.LiveAsync(target);
            if (key != _selected) return; // the user moved on while we waited
            if (view is not null && ShowLive(d, view)) return;
        }

        if (d.Role == DeviceRole.Self && !_source.Connected)
        {
            // No bridge: this PC from the Tray's own collectors, every other second.
            if (_shownKey == key && _ticks % 2 != 0) return;
            var m = await Task.Run(() => _metrics.CollectAsync(CancellationToken.None));
            var disks = await Task.Run(() => _disk.CollectAsync(CancellationToken.None));
            var net = await Task.Run(() => _network.CollectAsync(CancellationToken.None));
            if (key != _selected) return;
            SetPill(LedOn, L.PillThisPc);
            Show(key, PcSummaryView.BuildLocal(m.Success ? m.Snapshot : null, disks.Success ? disks.Snapshot ?? Array.Empty<DiskSnapshot>() : Array.Empty<DiskSnapshot>(),
                net.Success ? net.Snapshot : null, !_source.FeatureEnabled ? L.DetSelfFeatureOff : L.DetSelfNoAgent));
            return;
        }

        // Last reports only: phones, PCs seen through another PC, and PCs that can't be viewed live now.
        if (_shownKey == key && _ticks % 4 != 1) return;
        if (!d.IsPc)
        {
            SetPill(d.Online ? LedOn : Idle, d.Online ? L.PillOnline(Ago(d)) : L.PillOffline(Ago(d)));
            Show(key, PhoneView.Build(d));
            return;
        }
        SetPill(d.Online ? LedOn : Idle, d.Online ? L.PillLastReport(Ago(d)) : L.PillOffline(Ago(d)));
        Show(key, PcSummaryView.Build(d, SummaryReason(d)));
    }

    /// <summary>Shows a live answer; false when there is nothing live to show (then the last report is shown).</summary>
    private bool ShowLive(DeviceItem d, LiveView view)
    {
        var snapshot = view.Snapshot;
        switch (view.State)
        {
            case LiveViewState.Live when snapshot is not null:
                SetPill(LedOn, L.PillLive(ViaLabel(view.Via)));
                LiveViewFor(d).Notice(null);
                _live!.Update(snapshot);
                return true;
            case LiveViewState.Connecting:
                SetPill(Accent, d.Role switch
                {
                    DeviceRole.Self => L.PillStarting,
                    DeviceRole.LinkedHere => L.PillAsking,
                    _ => L.PillConnecting,
                });
                if (snapshot is not null)
                {
                    LiveViewFor(d).Notice(L.DetReconnecting);
                    _live!.Update(snapshot);
                }
                else if (_shownKey != d.Key)
                {
                    Show(d.Key, Waiting(d.Role switch
                    {
                        DeviceRole.Self => L.DetCollecting,
                        DeviceRole.LinkedHere => L.DetAskingShare(d.Name),
                        _ => L.DetConnectingTo(d.Name),
                    }));
                }
                return true;
            case LiveViewState.Unreachable when snapshot is not null:
                SetPill(LedOff, L.PillNoAnswer);
                LiveViewFor(d).Notice(L.DetStale(view.At is { } at ? Fmt.Ago(at, DateTimeOffset.UtcNow) : "—"));
                _live!.Update(snapshot);
                return true;
            default:
                return false;
        }
    }

    private PcLiveView LiveViewFor(DeviceItem d)
    {
        if (_live is null || _liveKey != d.Key || _shownKey != d.Key)
        {
            if (!_histories.TryGetValue(d.Key, out var h)) _histories[d.Key] = h = new History();
            _live = new PcLiveView(h);
            _liveKey = d.Key;
            Show(d.Key, _live.Root);
        }
        return _live;
    }

    private void Show(string key, FrameworkElement content)
    {
        DetailHost.Content = content;
        if (_liveKey == key && content != _live?.Root) _liveKey = null;
        _shownKey = key;
    }

    private static FrameworkElement Waiting(string text)
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
        var t = Text(text, 13, OnSlabSoft, wrap: true);
        t.TextAlignment = TextAlignment.Center;
        t.MaxWidth = 560;
        p.Children.Add(t);
        var bar = new MeterBar { Width = 220, Margin = new Thickness(0, 16, 0, 0) };
        bar.Set(0, MeterOk);
        p.Children.Add(bar);
        return SlabPanel(p);
    }

    private string? SummaryReason(DeviceItem d)
    {
        if (d.Role == DeviceRole.ViaHub) return L.DetViaHubOnly(d.Via ?? "");
        if (!_source.Connected) return null;
        if (_source.LiveUnsupported) return L.DetAgentTooOld;
        if (!d.Online) return d.Role == DeviceRole.Hub && d.Error == "rejected" ? L.DevHubRejected : L.DetPcOffline;
        if (d.Role == DeviceRole.LinkedHere && d.Status?.AppVersion is { } v) return L.DetPcTooOld(v);
        return L.DetNoLiveYet;
    }

    private static string ViaLabel(string? via) => via switch
    {
        MobileTransport.Lan => "LAN",
        MobileTransport.Tailscale => "Tailscale",
        MobileTransport.Cloudflare => "Cloudflare",
        MobileTransport.Loopback => "loopback",
        "shared" => L.ViaShared,
        "self" => L.PillThisPc,
        _ => via ?? "",
    };

    private static string Ago(DeviceItem d) => d.LastSeen is { } at ? Fmt.Ago(at, DateTimeOffset.UtcNow) : "—";

    private void SetPill(Brush led, string text)
    {
        LiveLed.Fill = led;
        LiveText.Text = text;
    }

    private void RenderHeader(DeviceItem d)
    {
        HeaderGlyphBox.Child = Glyph(d.IsPc, OnSlab, 24);
        HeaderName.Text = d.Name;
        var s = d.Status;
        var parts = new List<string>();
        if (!d.IsPc && d.Model is { } model) parts.Add(model);
        if (s?.Os is { } os) parts.Add(os);
        if (s?.AppVersion is { } v) parts.Add("Homebase v" + v);
        parts.Add(d.Role switch
        {
            DeviceRole.Self => L.HeadSelf,
            DeviceRole.Hub => L.HeadHub,
            DeviceRole.LinkedHere => L.HeadLinkedHere,
            DeviceRole.Phone => L.HeadPhone,
            _ => L.DevVia(d.Via ?? ""),
        });
        HeaderSub.Text = string.Join("  ·  ", parts);

        HeaderChips.Children.Clear();
        WarningBox.Visibility = Visibility.Collapsed;
        if (d.Role == DeviceRole.Self && _connections is { } c)
        {
            Chip(L.DashForwarding, c.Forwarding ? (c.AutostartOff ? ChipTone.Warn : ChipTone.On) : ChipTone.Off, SettingsTab.Forwarding);
            Chip(L.ChipPhoneLink, c.Phone ? ChipTone.On : ChipTone.Off, SettingsTab.Phone);
            Chip(L.DashTelegram, c.Readiness is { TokenConfigured: true, ChatConfigured: true } ? ChipTone.On : ChipTone.Off, SettingsTab.Telegram);
            if (c.Forwarding && c.AutostartOff)
            {
                WarningText.Text = L.ForwardingOnAutostartOff;
                WarningBox.Visibility = Visibility.Visible;
            }
        }
        else if (d.Role == DeviceRole.Hub && d.Phones > 0)
        {
            Chip(L.DevHubPhones(d.Phones), ChipTone.Off, SettingsTab.Phone);
        }
    }

    private enum ChipTone { On, Off, Warn }

    /// <summary>A status chip; clicking it opens the Settings area that changes it.</summary>
    private void Chip(string text, ChipTone tone, SettingsTab area)
    {
        var (bg, fg, mark) = tone switch
        {
            ChipTone.On => (Brush(0xE1, 0xF3, 0xE6), Brush(0x1E, 0x6B, 0x34), "● "),
            ChipTone.Warn => (Brush(0xFF, 0xEE, 0xC8), Brush(0x8A, 0x5A, 0x00), "▲ "),
            _ => (Brush(0xE8, 0xEB, 0xF4), InkSoft, "○ "),
        };
        var pill = Pill(mark + text, bg, fg, 11);
        pill.Margin = new Thickness(0, 0, 6, 4);
        pill.Cursor = Cursors.Hand;
        pill.ToolTip = L.ChipOpensSettings;
        pill.MouseLeftButtonUp += (_, _) => ShowTab(DashTab.Settings, area);
        HeaderChips.Children.Add(pill);
    }

    private static Brush Brush(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    // ── Service badge ──

    private static bool SafeForwardingEnabled()
    {
        try { return TrayConfig.IsNotificationForwardingEnabled(); } catch { return false; }
    }

    private static ServiceStatusInfo ReadServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status; // throws if the service does not exist
            return new ServiceStatusInfo(status.ToString(), status == ServiceControllerStatus.Running, Installed: true);
        }
        catch
        {
            return new ServiceStatusInfo("", false, Installed: false);
        }
    }

    private void ApplyServiceStatus(ServiceStatusInfo svc)
    {
        if (svc.Running)
        {
            AgentStatusText.Text = L.BadgeRunning.TrimStart('●', ' ');
            StatusLed.Fill = LedOn;
        }
        else if (!svc.Installed)
        {
            AgentStatusText.Text = L.BadgeNotInstalled.TrimStart('●', ' ');
            StatusLed.Fill = Idle;
        }
        else
        {
            AgentStatusText.Text = L.BadgeStopped(svc.StatusText).TrimStart('●', ' ');
            StatusLed.Fill = LedOff;
        }
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e) => ShowTab(DashTab.Settings, SettingsTab.Phone);

    /// <summary>
    /// Shows the window if it is hidden (near the tray, as a tray click does), otherwise restores and
    /// raises it where it is — for the tray menu, a notification balloon and the setup checklist.
    /// </summary>
    public void BringForward()
    {
        if (!IsVisible)
        {
            ShowNearTray();
            return;
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    // Tray-popup behaviour: anchor bottom-right above the taskbar and bring it to the foreground.
    // Closing (X) hides rather than destroys, so the same instance reopens on the next tray click.
    // It intentionally does NOT auto-hide on deactivation: launched from the WinForms tray icon this is
    // a background process, so the window used to lose focus and hide itself before it even painted.
    public void ShowNearTray()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Width = Math.Min(Width, wa.Width - 16);
        Height = Math.Min(Height, wa.Height - 16);
        Left = Math.Max(wa.Left, wa.Right - Width - 8);
        Top = Math.Max(wa.Top, wa.Bottom - Height - 8);

        Show();
        // A plain Activate() from a background app often fails to take the foreground; the brief
        // Topmost flip reliably raises the window to the front without pinning it always-on-top.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_preview) DashboardPrefs.Save(this, _selected);
        // During a programmatic shutdown (e.g. an auto-update restart) let the close proceed;
        // otherwise a tray click / X hides the dashboard so the same instance reopens later.
        if (App.IsShuttingDown || _preview) { base.OnClosing(e); return; }
        e.Cancel = true;
        Hide();
    }

    private readonly record struct ServiceStatusInfo(string StatusText, bool Running, bool Installed);

    private sealed record Connections(ReadinessSnapshot Readiness, bool Forwarding, bool Phone, bool AutostartOff);
}

/// <summary>The dashboard's size and selected device, per Windows user (<c>%LOCALAPPDATA%\Homebase\dashboard.json</c>).</summary>
internal static class DashboardPrefs
{
    private static string PathName => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "dashboard.json");

    private sealed record Prefs(double Width, double Height, string? Selected);

    public static void Load(Window w, out string? selected)
    {
        selected = null;
        try
        {
            if (!File.Exists(PathName)) return;
            var p = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PathName));
            if (p is null) return;
            if (p.Width >= w.MinWidth && p.Width < 4000) w.Width = p.Width;
            if (p.Height >= w.MinHeight && p.Height < 3000) w.Height = p.Height;
            selected = p.Selected;
        }
        catch (Exception) { /* a broken file just means the defaults */ }
    }

    public static void Save(Window w, string? selected)
    {
        try
        {
            if (w.WindowState != WindowState.Normal) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(new Prefs(w.ActualWidth > 0 ? w.ActualWidth : w.Width,
                w.ActualHeight > 0 ? w.ActualHeight : w.Height, selected)));
        }
        catch (Exception) { /* preferences are a convenience */ }
    }
}
