using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Windows;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;

namespace LocalOpsBot.Tray;

/// <summary>
/// The tray popup: what this PC is doing right now and whether its connections are set up.
/// Read-only by design — configuration lives in <see cref="SettingsWindow"/>, opened from the
/// button at the bottom (<see cref="OpenSettingsRequested"/>) or the tray menu.
/// </summary>
public partial class DashboardWindow : ThemedWindow
{
    private const string ServiceName = "Homebase.Agent";

    private readonly ISystemMetricsCollector _metrics = new WindowsSystemMetricsCollector();
    private readonly IDiskCollector _disk = new WindowsDiskCollector();
    private readonly INetworkStatusChecker _network = new WindowsNetworkStatusChecker();
    private readonly ConnectionReadiness _readiness = new();
    private readonly DispatcherTimer _timer;
    private bool _refreshing;
    private int _ticks;

    /// <summary>Raised by the "Settings" button; the tray opens the settings window.</summary>
    public event Action? OpenSettingsRequested;

    public DashboardWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();

        // Refresh the connection summary whenever the window is shown again — the user may just
        // have changed something in Settings.
        IsVisibleChanged += (_, _) => { if (IsVisible) { _ticks = 0; Refresh(); _timer.Start(); } else _timer.Stop(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async void Refresh()
    {
        // Timer ticks are re-entrant if a collection runs long; skip overlapping refreshes.
        // Safe without locking because this only ever runs on the UI thread.
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            // The WMI-backed collectors run synchronously; do the work off the UI thread
            // so the window never stutters, then apply results back on the UI thread.
            var metrics = await Task.Run(() => _metrics.CollectAsync(CancellationToken.None));
            var disks = await Task.Run(() => _disk.CollectAsync(CancellationToken.None));
            var network = await Task.Run(() => _network.CollectAsync(CancellationToken.None));
            var service = await Task.Run(ReadServiceStatus);

            ApplyServiceStatus(service);
            ApplyMetrics(metrics);
            ApplyNetwork(network);
            ApplyDisks(disks);

            // Other devices report every 30 s (PCs) or less often (phones); every ~9 s is plenty.
            if (_ticks % 3 == 0) await RefreshDevicesAsync();

            // Config-backed rows change rarely; re-read them every ~30 s rather than every tick.
            if (_ticks++ % 10 == 0)
            {
                var readiness = await Task.Run(_readiness.Probe);
                var forwarding = await Task.Run(SafeForwardingEnabled);
                var phone = await Task.Run(SafePhoneEnabled);
                ApplyConnections(readiness, forwarding, phone);
            }
        }
        catch
        {
            // Transient collection error — keep the last shown values.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (MobileHost.Client is not { IsConnected: true } client)
        {
            ShowDeviceMessage(MobileHost.IsEnabled() ? L.DevNone : L.DevFeatureOff);
            return;
        }
        DeviceOverview? overview = null;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpDevicesOverview);
            if ((bool?)r["ok"] == true) overview = r["payload"]?.Deserialize<DeviceOverview>(MobileProtocol.Json);
        }
        catch (Exception ex) when (ex is System.IO.IOException or TaskCanceledException or OperationCanceledException) { }
        if (overview is null) return; // keep what is shown
        ApplyDevices(overview);
    }

    private void ShowDeviceMessage(string text)
    {
        DevicePanel.Children.Clear();
        DevicePanel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Type.Micro"), TextWrapping = TextWrapping.Wrap });
    }

    private void ApplyDevices(DeviceOverview overview)
    {
        DevicePanel.Children.Clear();
        var shown = new HashSet<(string, string)>();
        foreach (var d in overview.Here.Devices)
        {
            shown.Add((d.Kind, d.Name));
            AddDeviceRow(d.Kind, d.Name, DeviceText.Entry(d), d.Online);
        }
        foreach (var h in overview.Hubs)
        {
            AddDeviceRow(DeviceKind.Pc, h.PcName, DeviceText.Hub(h), h.Online);
            foreach (var d in h.Devices)
            {
                // Skip this PC itself, and a phone already listed because it is paired here too.
                if (d.DeviceId == h.SelfDeviceId || !shown.Add((d.Kind, d.Name))) continue;
                AddDeviceRow(d.Kind, d.Name, DeviceText.Entry(d, h.PcName), d.Online);
            }
        }
        if (DevicePanel.Children.Count == 0) ShowDeviceMessage(L.DevNone);
    }

    private void AddDeviceRow(string kind, string name, string detail, bool online)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chip = new Border
        {
            Style = (Style)FindResource("Chip"),
            Background = (Brush)FindResource(online ? "Brush.StatusOk" : "Brush.Hairline"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = kind == DeviceKind.Pc ? L.DevKindPc : L.DevKindPhone,
                Style = (Style)FindResource("ChipText"),
            },
        };
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("Type.BodyStrong") });
        text.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Type.Micro"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(chip);
        row.Children.Add(text);
        DevicePanel.Children.Add(row);
    }

    private static bool SafeForwardingEnabled()
    {
        try { return TrayConfig.IsNotificationForwardingEnabled(); } catch { return false; }
    }

    private static bool SafePhoneEnabled()
    {
        try { return MobileHost.IsEnabled(); } catch { return false; }
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
            AgentStatusText.Text = L.BadgeRunning;
            StatusBadge.Background = (Brush)FindResource("Brush.StatusOnline");
        }
        else if (!svc.Installed)
        {
            AgentStatusText.Text = L.BadgeNotInstalled;
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // gray
        }
        else
        {
            AgentStatusText.Text = L.BadgeStopped(svc.StatusText);
            StatusBadge.Background = (Brush)FindResource("Brush.StatusBad");
        }
    }

    private void ApplyMetrics(CollectorResult<SystemMetricSnapshot> result)
    {
        if (!result.Success || result.Snapshot is null)
        {
            UptimeText.Text = L.Unknown;
            CpuBar.Value = 0; CpuText.Text = "—";
            RamBar.Value = 0; RamText.Text = "—";
            return;
        }

        var s = result.Snapshot;
        var u = s.Uptime;
        UptimeText.Text = u.Days > 0 ? L.UptimeDays(u.Days, u.Hours, u.Minutes) : L.UptimeHours((int)u.TotalHours, u.Minutes);

        if (s.CpuUsagePercent is double cpu)
        {
            CpuBar.Value = cpu;
            CpuBar.Foreground = GaugeBrush(cpu);
            CpuText.Text = $"{cpu:F0}%";
        }
        else { CpuBar.Value = 0; CpuText.Text = "—"; }

        if (s.TotalMemoryBytes is long total && s.AvailableMemoryBytes is long avail && total > 0)
        {
            var usedGb = (total - avail) / (1024.0 * 1024 * 1024);
            var totalGb = total / (1024.0 * 1024 * 1024);
            var pct = s.MemoryUsagePercent ?? (usedGb / totalGb * 100);
            RamBar.Value = pct;
            RamBar.Foreground = GaugeBrush(pct);
            RamText.Text = $"{pct:F0}% · {usedGb:F1} / {totalGb:F1} GB";
        }
        else { RamBar.Value = 0; RamText.Text = "—"; }
    }

    private void ApplyNetwork(CollectorResult<NetworkStatusSnapshot> result)
    {
        if (result.Success && result.Snapshot is { } s)
            NetworkText.Text = s.IsOnline ? L.Online(s.PrimaryIPv4 ?? L.NoIp) : L.Offline;
        else
            NetworkText.Text = L.Unknown;
    }

    // One row per ready drive: letter, a gauge of the used share (same thresholds as CPU/RAM),
    // and the free space — a full disk is the thing to notice, so the bar carries the glance.
    private void ApplyDisks(CollectorResult<IReadOnlyList<DiskSnapshot>> result)
    {
        DiskPanel.Children.Clear();
        if (result.Success && result.Snapshot is not null)
        {
            foreach (var d in result.Snapshot)
            {
                if (!d.IsReady || d.TotalBytes <= 0) continue;
                var freeGb = d.FreeBytes / (1024.0 * 1024 * 1024);
                var totalGb = d.TotalBytes / (1024.0 * 1024 * 1024);
                var usedPct = 100.0 * (d.TotalBytes - d.FreeBytes) / d.TotalBytes;

                // Value sits beside the bar, not on it: text over a red or indigo fill is unreadable.
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

                var name = new TextBlock { Text = d.Name.TrimEnd('\\'), Style = (Style)FindResource("Type.Label"), VerticalAlignment = VerticalAlignment.Center };
                var gauge = new ProgressBar { Style = (Style)FindResource("Gauge"), Height = 16, Value = usedPct, Foreground = GaugeBrush(usedPct) };
                var free = new TextBlock
                {
                    Text = L.DiskFree(freeGb, totalGb),
                    Style = (Style)FindResource("Type.BodyStrong"),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(gauge, 1);
                Grid.SetColumn(free, 2);
                row.Children.Add(name);
                row.Children.Add(gauge);
                row.Children.Add(free);
                DiskPanel.Children.Add(row);
            }
        }
        if (DiskPanel.Children.Count == 0)
            DiskPanel.Children.Add(new TextBlock { Text = "—", Style = (Style)FindResource("Type.Micro") });
    }

    private void ApplyConnections(ReadinessSnapshot r, bool forwarding, bool phone)
    {
        if (r is { TokenConfigured: true, ChatConfigured: true, PrimaryChatId: long chat })
        {
            SetChip(TelegramChip, TelegramChipText, "Brush.StatusOnline", L.ChipReady);
            TelegramText.Text = L.TelegramConnected(chat);
        }
        else
        {
            SetChip(TelegramChip, TelegramChipText, "Brush.StatusWarn", L.ChipSetup);
            TelegramText.Text = L.TelegramNotSetUp;
        }

        SetChip(ForwardingChip, ForwardingChipText, forwarding ? "Brush.StatusOk" : "Brush.Hairline", forwarding ? L.ChipOn : L.ChipOff);
        ForwardingText.Text = forwarding ? L.ForwardingOn : L.ForwardingOff;

        SetChip(PhoneChip, PhoneChipText, phone ? "Brush.StatusOk" : "Brush.Hairline", phone ? L.ChipOn : L.ChipOff);
        PhoneText.Text = phone ? L.PhoneOn : L.PhoneOff;
    }

    private void SetChip(Border chip, TextBlock text, string brushKey, string label)
    {
        chip.Background = (Brush)FindResource(brushKey);
        text.Text = label;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // Tray-popup behaviour: anchor bottom-right above the taskbar and bring it to the foreground.
    // Closing (X or CLOSE) hides rather than destroys, so the same instance reopens on the next
    // tray click. It intentionally does NOT auto-hide on deactivation: launched from the WinForms
    // tray icon this is a background process, so the window used to lose focus and hide itself
    // before it even painted — showing only an empty flash and then vanishing on the next click.
    public void ShowNearTray()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 8;
        Top = Math.Max(wa.Top, wa.Bottom - Height - 8);

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
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
        // During a programmatic shutdown (e.g. an auto-update restart) let the close proceed;
        // otherwise a tray click / X hides the dashboard so the same instance reopens later.
        if (App.IsShuttingDown) { base.OnClosing(e); return; }
        e.Cancel = true;
        Hide();
    }

    // Cool by default (chrome-indigo); warms to amber then brand red at thresholds.
    private static Brush GaugeBrush(double pct) =>
        (Brush)System.Windows.Application.Current.FindResource(
            pct >= 90 ? "Brush.StatusBad" : pct >= 75 ? "Brush.StatusWarn" : "Brush.StatusOk");

    private readonly record struct ServiceStatusInfo(string StatusText, bool Running, bool Installed);
}
