using LocalOpsBot.Core.Delivery;

namespace LocalOpsBot.Infrastructure.Telegram;

public sealed class TelegramNotificationRenderer
{
    public string Render(OutboundNotification notification)
    {
        var icon = notification.Priority switch
        {
            OutboundPriority.Critical => "\U0001f534",
            OutboundPriority.Warning => "\U0001f7e1",
            OutboundPriority.Recovery => "\U0001f7e2",
            _ => "ℹ️"
        };

        var title = HtmlEscape(notification.Title);
        var body = string.IsNullOrWhiteSpace(notification.Body)
            ? string.Empty
            : $"\n{HtmlEscape(notification.Body)}";

        return $"{icon} <b>{title}</b>{body}";
    }

    private static string HtmlEscape(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
