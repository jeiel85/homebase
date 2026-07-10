using System.Diagnostics;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Windows;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

public partial class SettingsWindow : ThemedWindow
{
    private const string ServiceName = "Homebase.Agent";

    private readonly ISystemMetricsCollector _metrics = new WindowsSystemMetricsCollector();
    private readonly IDiskCollector _disk = new WindowsDiskCollector();
    private readonly INetworkStatusChecker _network = new WindowsNetworkStatusChecker();
    private readonly UpdateCoordinator _updates;
    private readonly DispatcherTimer _timer;
    private bool _refreshing;

    internal SettingsWindow(UpdateCoordinator updates)
    {
        InitializeComponent();
        _updates = updates;

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        AboutVersionText.Text = $"Homebase v{ver?.ToString(3) ?? "?"}";
        UpdateStatusText.Text = $"Current version: v{_updates.CurrentVersion}";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { Refresh(); LoadForwardingState(); LoadMonitors(); _timer.Start(); };
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

    private static ServiceStatusInfo ReadServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status; // throws if the service does not exist
            return new ServiceStatusInfo(status.ToString(), status == ServiceControllerStatus.Running);
        }
        catch
        {
            return new ServiceStatusInfo("Not installed", false);
        }
    }

    private void ApplyServiceStatus(ServiceStatusInfo svc)
    {
        if (svc.Running)
        {
            AgentStatusText.Text = "● Agent running";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)); // green
        }
        else if (svc.StatusText == "Not installed")
        {
            AgentStatusText.Text = "● Agent not installed";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // gray
        }
        else
        {
            AgentStatusText.Text = $"● Agent {svc.StatusText.ToLowerInvariant()}";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)); // red
        }
    }

    private void ApplyMetrics(CollectorResult<SystemMetricSnapshot> result)
    {
        if (!result.Success || result.Snapshot is null)
        {
            UptimeText.Text = "unknown";
            CpuBar.Value = 0; CpuText.Text = "—";
            RamBar.Value = 0; RamText.Text = "—";
            return;
        }

        var s = result.Snapshot;
        var u = s.Uptime;
        UptimeText.Text = u.Days > 0
            ? $"{u.Days}d {u.Hours:00}h {u.Minutes:00}m"
            : $"{(int)u.TotalHours:00}h {u.Minutes:00}m";

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
            RamText.Text = $"{usedGb:F1} / {totalGb:F1} GB ({pct:F0}%)";
        }
        else { RamBar.Value = 0; RamText.Text = "—"; }
    }

    private void ApplyNetwork(CollectorResult<NetworkStatusSnapshot> result)
    {
        if (result.Success && result.Snapshot is { } s)
            NetworkText.Text = s.IsOnline ? $"Online ({s.PrimaryIPv4 ?? "no IP"})" : "Offline";
        else
            NetworkText.Text = "unknown";
    }

    private void ApplyDisks(CollectorResult<IReadOnlyList<DiskSnapshot>> result)
    {
        DiskPanel.Children.Clear();
        if (result.Success && result.Snapshot is not null)
        {
            foreach (var d in result.Snapshot)
            {
                if (!d.IsReady) continue;
                var freeGb = d.FreeBytes / (1024.0 * 1024 * 1024);
                var totalGb = d.TotalBytes / (1024.0 * 1024 * 1024);
                DiskPanel.Children.Add(new TextBlock
                {
                    Text = $"{d.Name}  {freeGb:F0} GB free / {totalGb:F0} GB",
                    Style = (Style)FindResource("Type.Body"),
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
        }
        if (DiskPanel.Children.Count == 0)
            DiskPanel.Children.Add(new TextBlock { Text = "—", Style = (Style)FindResource("Type.Micro") });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // ── Notification forwarding ────────────────────────────────────────────────────────────────
    // Reflect the persisted config state. Setting IsChecked here does NOT raise Click, so it never
    // re-triggers the toggle handler below.
    private void LoadForwardingState()
    {
        try { ForwardingEnabledCheckBox.IsChecked = TrayConfig.IsNotificationForwardingEnabled(); }
        catch { ForwardingEnabledCheckBox.IsChecked = false; }
        UpdateAppSelectionUi();
    }

    // App selection only makes sense while forwarding is on. Populating reads the apps the tray has
    // seen plus the current allow-list, so a checked box means "forward this app".
    private void UpdateAppSelectionUi()
    {
        var enabled = ForwardingEnabledCheckBox.IsChecked == true;
        AppSelectionPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) PopulateAppList();
    }

    private void PopulateAppList()
    {
        AppListPanel.Children.Clear();
        var seen = ForwardingApps.ReadSeenApps();
        var blocked = new HashSet<string>(ForwardingApps.ReadBlockList(), StringComparer.OrdinalIgnoreCase);

        if (seen.Count == 0)
        {
            AppListPanel.Children.Add(new TextBlock
            {
                Text = "No apps seen yet. Trigger a notification, then tap REFRESH.",
                Style = (Style)FindResource("Type.Micro"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var app in seen.OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase))
        {
            AppListPanel.Children.Add(new CheckBox
            {
                Content = app,
                IsChecked = blocked.Contains(app), // checked = excluded from forwarding
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = (Brush)FindResource("Brush.Ink")
            });
        }
    }

    private void SaveApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var excluded = AppListPanel.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Content?.ToString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            ForwardingApps.WriteBlockList(excluded);
            AppsStatusText.Text = excluded.Count == 0
                ? "Saved — forwarding all apps."
                : $"Saved — excluding {excluded.Count} app(s). Applies within seconds.";
        }
        catch (Exception ex)
        {
            AppsStatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        PopulateAppList();
        AppsStatusText.Text = string.Empty;
    }

    private async void ForwardingToggle_Click(object sender, RoutedEventArgs e)
    {
        var enable = ForwardingEnabledCheckBox.IsChecked == true;

        var confirmed = MessageDialog.Show(
            enable ? "Enable Notification Forwarding" : "Disable Notification Forwarding",
            enable
                ? "This forwards your Windows notifications (title and body) to your Telegram chat. " +
                  "Saving needs administrator approval, and the tray restarts to start listening. Continue?"
                : "Stop forwarding Windows notifications to Telegram? Saving needs administrator approval.",
            primary: enable ? "Enable" : "Disable", secondary: "Cancel");
        if (!confirmed)
        {
            ForwardingEnabledCheckBox.IsChecked = !enable; // revert — nothing changed
            return;
        }

        ForwardingEnabledCheckBox.IsEnabled = false;
        try
        {
            var saved = await NotificationForwardingConfigurator.SetEnabledAsync(enable);
            if (!saved)
            {
                ForwardingEnabledCheckBox.IsChecked = !enable; // UAC declined
                MessageDialog.Show("Notification Forwarding",
                    "Couldn't save the setting — administrator approval is required. Nothing was changed.");
                return;
            }

            // The tray must restart to start/stop the notification listener with the new state.
            var restart = MessageDialog.Show("Restart Homebase",
                enable
                    ? "Forwarding is enabled. The tray needs to restart to start listening — restart now?"
                    : "Forwarding is disabled. The tray needs to restart to stop listening — restart now?",
                primary: "Restart Now", secondary: "Later");
            if (restart) RestartTray();
        }
        catch (Exception ex)
        {
            ForwardingEnabledCheckBox.IsChecked = !enable;
            MessageDialog.Show("Notification Forwarding", $"Couldn't save the setting:\n{ex.Message}");
        }
        finally
        {
            ForwardingEnabledCheckBox.IsEnabled = true;
            UpdateAppSelectionUi();
        }
    }

    private static void RestartTray()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { /* best-effort — worst case the user relaunches from the Start menu */ }
        App.IsShuttingDown = true;
        System.Windows.Application.Current.Shutdown();
    }

    // ── Updates ────────────────────────────────────────────────────────────────────────────────
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var status = new Progress<string>(text => UpdateStatusText.Text = text);
            await _updates.RunInteractiveAsync(status);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // ── Monitors ─────────────────────────────────────────────────────────────────────────────────
    private void LoadMonitors()
    {
        try
        {
            var m = MonitorsConfig.Read();
            ProcessesBox.Text = string.Join(Environment.NewLine, m.Processes);
            ServicesBox.Text = string.Join(Environment.NewLine, m.Services);
            HttpBox.Text = string.Join(Environment.NewLine, m.HttpEndpoints);
            TcpBox.Text = string.Join(Environment.NewLine, m.TcpPorts);
        }
        catch { /* leave blank if the config can't be read */ }
    }

    private async void SaveMonitors_Click(object sender, RoutedEventArgs e)
    {
        SaveMonitorsButton.IsEnabled = false;
        MonitorsStatusText.Text = "Saving — approve the admin prompt…";
        try
        {
            var saved = await MonitorsConfigurator.SaveAsync(
                Lines(ProcessesBox), Lines(ServicesBox), Lines(HttpBox), Lines(TcpBox));
            MonitorsStatusText.Text = saved
                ? "Saved. The Agent is restarting to apply."
                : "Not saved — administrator approval is required.";
        }
        catch (Exception ex)
        {
            MonitorsStatusText.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            SaveMonitorsButton.IsEnabled = true;
        }
    }

    private static IEnumerable<string> Lines(TextBox box) =>
        box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

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

    private readonly record struct ServiceStatusInfo(string StatusText, bool Running);
}
