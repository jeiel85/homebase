using LocalOpsBot.Core.Alerts;

namespace LocalOpsBot.Core.Commands;

public sealed class AlertsCommandHandler : ICommandHandler
{
    private readonly IAlertStore _alertStore;

    public string CommandName => "alerts";
    public string Description => "Recent alert history";

    public AlertsCommandHandler(IAlertStore alertStore)
    {
        _alertStore = alertStore;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        const int defaultCount = 5;
        var count = defaultCount;
        if (command.Args.Count > 0 && int.TryParse(command.Args[0], out var n))
            count = Math.Clamp(n, 1, 20);

        var recent = await _alertStore.GetRecentAsync(count, ct);
        if (recent.Count == 0)
            return new CommandResult(true, "<b>\U0001f4cb Recent Alerts</b>\n\nNo recent alerts.");

        var lines = new List<string> { "<b>\U0001f4cb Recent Alerts</b>\n" };
        foreach (var alert in recent)
        {
            var icon = alert.Severity switch
            {
                "Critical" => "\U0001f525",
                "Warning" => "\u26a0\ufe0f",
                "Recovery" => "\u2705",
                _ => "\U0001f514"
            };
            var time = alert.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
            var status = alert.Status == "Sent" ? "\u2705" : "\u274c";
            lines.Add($"{icon} <b>{HtmlEscape(alert.Title)}</b> {status}");
            lines.Add($"  {alert.Kind} | {time} | {alert.Severity}");
            if (alert.Body != null)
                lines.Add($"  <code>{HtmlEscape(Truncate(alert.Body, 150))}</code>");
            lines.Add("");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static string HtmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
