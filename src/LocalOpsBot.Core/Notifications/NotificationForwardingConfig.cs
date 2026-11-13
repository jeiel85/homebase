using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Core.Notifications;

/// <summary>
/// The one place that decides whether Windows toast forwarding is on. Default <b>on</b> since
/// DECISIONS M-19 (users want almost every notification); an explicit
/// <c>notificationForwarding:enabled=false</c> is respected. Toasts still go only to encrypted
/// channels unless <c>notificationForwarding:telegramFallback</c> is set.
/// </summary>
public static class NotificationForwardingConfig
{
    public const string SectionName = "notificationForwarding";

    public static bool IsEnabled(IConfiguration config) =>
        config.GetSection(SectionName).GetValue("enabled", true);
}
