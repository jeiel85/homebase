using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class WatchCommandHandler : ICommandHandler
{
    private readonly IProcessCollector _processCollector;
    private readonly IWindowsServiceCollector _serviceCollector;
    private readonly IReadOnlyList<ProcessWatchConfig> _processWatches;
    private readonly IReadOnlyList<ServiceWatchConfig> _serviceWatches;

    public string CommandName => "watch";
    public string Description => "Combined process and service watch status";

    public WatchCommandHandler(
        IProcessCollector processCollector,
        IWindowsServiceCollector serviceCollector,
        IEnumerable<ProcessWatchConfig> processWatches,
        IEnumerable<ServiceWatchConfig> serviceWatches)
    {
        _processCollector = processCollector;
        _serviceCollector = serviceCollector;
        _processWatches = processWatches.ToList();
        _serviceWatches = serviceWatches.ToList();
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        if (_processWatches.Count == 0 && _serviceWatches.Count == 0)
            return new CommandResult(false, "No watches configured.\nAdd `processWatches` or `serviceWatches` to config.");

        var lines = new List<string> { "<b>\ud83d\udc40 Watch Status</b>\n" };

        if (_processWatches.Count > 0)
        {
            var processResults = await _processCollector.CollectAsync(_processWatches, ct);
            lines.Add("<b>\u2699 Process Watches</b>");
            foreach (var r in processResults)
            {
                var icon = r.IsRunning ? "\u2705" : "\u274c";
                lines.Add($"{icon} <b>{HtmlEscape(r.WatchName)}</b> ({r.InstanceCount} instance(s))");
            }
            lines.Add("");
        }

        if (_serviceWatches.Count > 0)
        {
            var serviceResults = await _serviceCollector.CollectAsync(_serviceWatches, ct);
            lines.Add("<b>\ud83d\udee1\ufe0f Service Watches</b>");
            foreach (var r in serviceResults)
            {
                var icon = r.IsExpectedStatus ? "\u2705" : "\u274c";
                lines.Add($"{icon} <b>{HtmlEscape(r.WatchName)}</b> ({r.Status})");
            }
            lines.Add("");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
