using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class StatusCommandHandler : ICommandHandler
{
    private readonly ISystemMetricsCollector _metrics;
    private readonly IDiskCollector _disk;
    private readonly INetworkStatusChecker _network;

    public string CommandName => "status";
    public string Description => "Full PC status summary";

    public StatusCommandHandler(
        ISystemMetricsCollector metrics,
        IDiskCollector disk,
        INetworkStatusChecker network)
    {
        _metrics = metrics;
        _disk = disk;
        _network = network;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var metricsResult = await _metrics.CollectAsync(ct);
        var diskResult = await _disk.CollectAsync(ct);
        var networkResult = await _network.CollectAsync(ct);

        var lines = new List<string>();

        // Header
        var hostName = metricsResult.Snapshot?.HostName ?? Environment.MachineName;
        lines.Add($"<b>\U0001f5a5 {HtmlEscape(hostName)} Status</b>\n");

        // Uptime
        if (metricsResult.Success && metricsResult.Snapshot != null)
        {
            var u = metricsResult.Snapshot.Uptime;
            var uptimeStr = u.Days > 0
                ? $"{u.Days}d {u.Hours:00}h {u.Minutes:00}m"
                : $"{(int)u.TotalHours:00}h {u.Minutes:00}m";
            lines.Add($"Uptime: <code>{uptimeStr}</code>");
        }
        else
        {
            lines.Add("Uptime: <code>unknown</code>");
        }

        // CPU
        if (metricsResult.Success && metricsResult.Snapshot?.CpuUsagePercent != null)
            lines.Add($"CPU: <code>{metricsResult.Snapshot.CpuUsagePercent:F0}%</code>");
        else
            lines.Add("CPU: <code>unknown</code>");

        // RAM
        if (metricsResult.Success && metricsResult.Snapshot?.TotalMemoryBytes != null
            && metricsResult.Snapshot.AvailableMemoryBytes != null)
        {
            var totalGb = metricsResult.Snapshot.TotalMemoryBytes.Value / (1024.0 * 1024 * 1024);
            var usedGb = (metricsResult.Snapshot.TotalMemoryBytes.Value - metricsResult.Snapshot.AvailableMemoryBytes.Value)
                         / (1024.0 * 1024 * 1024);
            var pct = metricsResult.Snapshot.MemoryUsagePercent ?? (usedGb / totalGb * 100);
            lines.Add($"RAM: <code>{usedGb:F1} / {totalGb:F1} GB ({pct:F0}%)</code>");
        }
        else
        {
            lines.Add("RAM: <code>unknown</code>");
        }

        // Network
        if (networkResult.Success && networkResult.Snapshot != null)
        {
            var status = networkResult.Snapshot.IsOnline ? "Online" : "Offline";
            var ip = networkResult.Snapshot.PrimaryIPv4 ?? "no IP";
            lines.Add($"Network: <code>{status} ({ip})</code>");
        }
        else
        {
            lines.Add("Network: <code>unknown</code>");
        }

        // Disk
        if (diskResult.Success && diskResult.Snapshot != null && diskResult.Snapshot.Count > 0)
        {
            lines.Add("\n<b>Disk</b>");
            foreach (var d in diskResult.Snapshot)
            {
                if (!d.IsReady) continue;
                var freeGb = d.FreeBytes / (1024.0 * 1024 * 1024);
                var totalGb = d.TotalBytes / (1024.0 * 1024 * 1024);
                lines.Add($"{d.Name}: <code>{freeGb:F1} GB free / {totalGb:F1} GB</code>");
            }
        }

        var text = string.Join("\n", lines);
        return new CommandResult(true, text);
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
