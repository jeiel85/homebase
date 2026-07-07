using LocalOpsBot.Core.Alerts;

namespace LocalOpsBot.Core.Commands;

public sealed class PolicyCommandHandler : ICommandHandler
{
    private readonly IStateStore _stateStore;
    private readonly AlertingOptions _options;

    public string CommandName => "policy";
    public string Description => "Show current alert policy settings";

    public PolicyCommandHandler(IStateStore stateStore, AlertingOptions options)
    {
        _stateStore = stateStore;
        _options = options;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var mutedUntilStr = await _stateStore.GetAsync("alert.muted_until", ct);
        var mutedInfo = "No mute active";
        if (DateTime.TryParse(mutedUntilStr, out var mutedUntil) && DateTime.UtcNow < mutedUntil)
            mutedInfo = $"Muted until {mutedUntil:yyyy-MM-dd HH:mm} UTC ({(mutedUntil - DateTime.UtcNow).TotalMinutes:F0} min remaining)";

        var lines = new List<string>
        {
            "<b>\ud83d\udce1 Alert Policy</b>\n",
            $"<b>Mute state:</b> {HtmlEscape(mutedInfo)}",
            $"<b>Dedup window:</b> {_options.DedupWindowSeconds}s",
            $"<b>Max messages/min:</b> {_options.MaxMessagesPerMinute}",
            $"<b>Max messages/hour:</b> {_options.MaxMessagesPerHour}",
            $"<b>Recovery alerts:</b> {(_options.SendRecoveryAlerts ? "On" : "Off")}",
            $"<b>Critical bypass mute:</b> {(_options.CriticalAlertsBypassMute ? "Yes" : "No")}",
            "",
            "Use /mute 1h to silence, /unmute to resume."
        };

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
