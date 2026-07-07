using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class ServicesCommandHandler : ICommandHandler
{
    private readonly IWindowsServiceCollector _serviceCollector;
    private readonly IReadOnlyList<ServiceWatchConfig> _watches;

    public string CommandName => "services";
    public string Description => "Windows service watch status";

    public ServicesCommandHandler(
        IWindowsServiceCollector serviceCollector,
        IEnumerable<ServiceWatchConfig> watches)
    {
        _serviceCollector = serviceCollector;
        _watches = watches.ToList();
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        if (_watches.Count == 0)
            return new CommandResult(false, "No service watches configured.\nAdd `serviceWatches` to config.");

        var results = await _serviceCollector.CollectAsync(_watches, ct);
        var lines = new List<string> { "<b>\u2699 Service Watch Status</b>\n" };

        foreach (var r in results)
        {
            var icon = r.IsExpectedStatus ? "\u2705" : "\u26a0\ufe0f";
            lines.Add($"{icon} <b>{HtmlEscape(r.WatchName)}</b>");
            lines.Add($"  Service: {HtmlEscape(r.ServiceName)}");
            lines.Add($"  Status: {r.Status ?? "Unknown"} (expected: {_watches.FirstOrDefault(w => w.ServiceName == r.ServiceName)?.ExpectedStatus ?? "Running"})");
            if (r.FailureReason != null)
                lines.Add($"  Error: {r.FailureReason}");
            lines.Add("");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
