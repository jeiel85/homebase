using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class UptimeCommandHandler : ICommandHandler
{
    private readonly ISystemMetricsCollector _metrics;

    public string CommandName => "uptime";
    public string Description => "System uptime";

    public UptimeCommandHandler(ISystemMetricsCollector metrics) => _metrics = metrics;

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var result = await _metrics.CollectAsync(ct);

        if (!result.Success || result.Snapshot == null)
            return new CommandResult(false, "Uptime unavailable.");

        var u = result.Snapshot.Uptime;
        var uptimeStr = u.Days > 0
            ? $"{u.Days}d {u.Hours:00}h {u.Minutes:00}m"
            : $"{(int)u.TotalHours:00}h {u.Minutes:00}m {u.Seconds:00}s";

        var host = result.Snapshot.HostName;
        var lines = new List<string>
        {
            $"<b>{host}</b>",
            $"Uptime: <code>{uptimeStr}</code>",
            $"Since: <code>{DateTimeOffset.UtcNow - u:yyyy-MM-dd HH:mm:ss UTC}</code>"
        };

        return new CommandResult(true, string.Join("\n", lines));
    }
}
