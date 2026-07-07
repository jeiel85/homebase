using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class EventsCommandHandler : ICommandHandler
{
    private readonly IEventLogWatcher _eventLogWatcher;
    private readonly EventLogOptions _options;

    public string CommandName => "events";
    public string Description => "Recent Windows Event Logs";

    public EventsCommandHandler(IEventLogWatcher eventLogWatcher, EventLogOptions options)
    {
        _eventLogWatcher = eventLogWatcher;
        _options = options;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var limit = 5;
        if (command.Args.Count > 0 && int.TryParse(command.Args[0], out var n))
            limit = Math.Clamp(n, 1, 20);

        var all = await _eventLogWatcher.PollAsync(_options, ct);
        var recent = all.Take(limit).ToList();

        if (recent.Count == 0)
            return new CommandResult(true, "<b>\U0001f4cb Recent Events</b>\n\nNo recent events found.");

        var lines = new List<string> { "<b>\U0001f4cb Recent Events</b>\n" };

        foreach (var e in recent)
        {
            var icon = e.Level == "Critical" ? "\U0001f525" : "\u26a0\ufe0f";
            var time = e.TimeCreated.ToLocalTime().ToString("MM-dd HH:mm");
            lines.Add($"{icon} <b>[{e.Level}]</b> {HtmlEscape(e.LogName)}");
            lines.Add($"  Provider: {HtmlEscape(e.ProviderName ?? "?")} (EventId: {e.EventId})");
            lines.Add($"  Time: {time}");
            if (e.Message != null)
                lines.Add($"  <code>{HtmlEscape(Truncate(e.Message, 200))}</code>");
            lines.Add("");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static string HtmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
