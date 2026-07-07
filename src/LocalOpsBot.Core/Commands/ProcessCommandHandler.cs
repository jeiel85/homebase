using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class ProcessCommandHandler : ICommandHandler
{
    private readonly IProcessCollector _processCollector;
    private readonly IReadOnlyList<ProcessWatchConfig> _watches;

    public string CommandName => "process";
    public string Description => "Watched process status";

    public ProcessCommandHandler(
        IProcessCollector processCollector,
        IEnumerable<ProcessWatchConfig> watches)
    {
        _processCollector = processCollector;
        _watches = watches.ToList();
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        if (_watches.Count == 0)
            return new CommandResult(false, "No process watches configured.\nAdd `processWatches` to config.");

        var results = await _processCollector.CollectAsync(_watches, ct);
        var lines = new List<string> { "<b>\u2699 Process Watch Status</b>\n" };

        foreach (var r in results)
        {
            var icon = r.IsRunning ? "\u2705" : "\u274c";
            lines.Add($"{icon} <b>{HtmlEscape(r.WatchName)}</b>");
            lines.Add($"  Process: {string.Join(", ", r.ProcessNames)}");
            lines.Add($"  Status: {(r.IsRunning ? "Running" : "Missing")} ({r.InstanceCount} instance(s))");
            lines.Add("");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
