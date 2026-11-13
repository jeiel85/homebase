using LocalOpsBot.Core.Localization;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Notifications;

/// <summary>
/// "PC booted" text as a plain title and body. Each channel renders it (Telegram escapes HTML,
/// the phone shows plain text), so nothing here is channel-specific.
/// </summary>
public static class BootMessageFormatter
{
    public static (string Title, string Body) Compose(HostInfoRecord host, DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "Korea Standard Time");
        var uptime = host.Uptime;
        var uptimeStr = uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours:00}h {uptime.Minutes:00}m"
            : $"{(int)uptime.TotalHours:00}h {uptime.Minutes:00}m {uptime.Seconds:00}s";

        var ip = host.PrimaryIPv4 ?? Strings.Unknown;

        return (
            Strings.BootDetected(host.MachineName),
            $"{Strings.TimeLabel}: {localNow:yyyy-MM-dd HH:mm:ss 'KST'}\n"
            + $"IP: {ip}\n"
            + $"{Strings.Uptime}: {uptimeStr}");
    }
}
