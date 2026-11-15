namespace LocalOpsBot.Core.Notifications;

/// <summary>
/// The PC's notification center (DECISIONS M-32): notifications relayed here from other devices,
/// plus this PC's own alerts (read from the alert log). Kept on the Agent so nothing is missed while
/// nobody is logged on; the Tray reads it over the mobile bridge.
/// </summary>
public static class NotificationHistoryRules
{
    /// <summary>How long entries are kept (relayed notifications and the alert log alike).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>At most this many relayed notifications are kept; the oldest go first.</summary>
    public const int MaxDeviceEntries = 500;

    /// <summary>
    /// Bodies are cut to this many characters when listed, so a page of long event-log alerts stays
    /// well inside the bridge's 1 MB frame.
    /// </summary>
    public const int MaxListedBody = 2000;

    /// <summary>Largest page the Tray may ask for.</summary>
    public const int MaxPage = 100;

    /// <summary>Clamps a requested page size to 1…<see cref="MaxPage"/>.</summary>
    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? 50, 1, MaxPage);
}

/// <summary>Kinds of <see cref="HistoryEntry"/>.</summary>
public static class HistoryKind
{
    /// <summary>Relayed from a phone or another PC.</summary>
    public const string Device = "device";

    /// <summary>One of this PC's own alerts (event log, watchdog, dev server, advisor).</summary>
    public const string Alert = "alert";
}

/// <summary>A relayed notification as it is stored (already masked).</summary>
public sealed record DeviceNotificationRecord(
    string? NotifId,
    string OriginId,
    string OriginName,
    string OriginKind,
    string App,
    string Title,
    string? Body,
    DateTimeOffset Ts,
    DateTimeOffset ReceivedAt);

/// <summary>
/// One line of the notification center. <c>At</c> is when this PC received or raised it.
/// For <see cref="HistoryKind.Device"/>: <c>Source</c> is the device name, <c>SourceKind</c> phone/pc,
/// <c>App</c> the app on that device. For <see cref="HistoryKind.Alert"/>: <c>App</c> is the alert kind,
/// with <c>Severity</c> and delivery <c>Status</c> (Sent/Failed).
/// </summary>
public sealed record HistoryEntry(
    string Kind,
    long Id,
    DateTimeOffset At,
    string? Source,
    string? SourceKind,
    string? OriginId,
    string App,
    string Title,
    string? Body,
    string? Severity = null,
    string? Status = null);

/// <summary><c>history.list</c> answer, newest first. <c>More</c> says older entries exist.</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Entries, bool More);

public interface INotificationHistoryStore
{
    Task AddAsync(DeviceNotificationRecord record, CancellationToken ct);

    /// <summary>Entries received strictly before <paramref name="before"/> (or the newest), newest first.</summary>
    Task<HistoryPage> ListAsync(DateTimeOffset? before, int limit, CancellationToken ct);

    /// <summary>Deletes every relayed notification. The alert log stays (it is also diagnostics).</summary>
    Task<int> ClearDevicesAsync(CancellationToken ct);

    /// <summary>Drops relayed notifications and alert-log rows older than <paramref name="cutoff"/>, and relayed ones beyond <paramref name="maxDeviceEntries"/>.</summary>
    Task<int> PruneAsync(DateTimeOffset cutoff, int maxDeviceEntries, CancellationToken ct);
}
