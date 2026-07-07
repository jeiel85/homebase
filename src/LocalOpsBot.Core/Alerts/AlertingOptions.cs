namespace LocalOpsBot.Core.Alerts;

public sealed class AlertingOptions
{
    public int DedupWindowSeconds { get; set; } = 600;
    public int MaxMessagesPerMinute { get; set; } = 10;
    public int MaxMessagesPerHour { get; set; } = 120;
    public bool SendRecoveryAlerts { get; set; } = true;
    public bool CriticalAlertsBypassMute { get; set; } = false;
}
