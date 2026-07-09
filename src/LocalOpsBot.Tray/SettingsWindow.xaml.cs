using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Windows;

namespace LocalOpsBot.Tray;

public partial class SettingsWindow : Window
{
    private const string ServiceName = "Homebase.Agent";

    private readonly ISystemMetricsCollector _metrics = new WindowsSystemMetricsCollector();
    private readonly IDiskCollector _disk = new WindowsDiskCollector();
    private readonly INetworkStatusChecker _network = new WindowsNetworkStatusChecker();
    private readonly DispatcherTimer _timer;
    private bool _refreshing;

    public SettingsWindow()
    {
        InitializeComponent();

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        AboutVersionText.Text = $"Homebase v{ver?.ToString(3) ?? "?"}";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { Refresh(); _timer.Start(); };
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
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
        }
        if (DiskPanel.Children.Count == 0)
            DiskPanel.Children.Add(new TextBlock { Text = "—", Foreground = Brushes.Gray });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // Tray-popup behaviour: anchor bottom-right above the taskbar; hide (not destroy)
    // when it loses focus or the user dismisses it, so the same instance can reopen.
    public void ShowNearTray()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 8;
        Top = Math.Max(wa.Top, wa.Bottom - Height - 8);
        Show();
        Activate();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // Cool by default (chrome-indigo); warms to amber then brand red at thresholds.
    private static Brush GaugeBrush(double pct) =>
        (Brush)System.Windows.Application.Current.FindResource(
            pct >= 90 ? "Brush.StatusBad" : pct >= 75 ? "Brush.StatusWarn" : "Brush.StatusOk");

    private readonly record struct ServiceStatusInfo(string StatusText, bool Running);
}
