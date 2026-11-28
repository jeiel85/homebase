using System.Text.Json;

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

    /// <summary>
    /// A <c>history.list</c> answer is trimmed to this many serialized bytes. The bridge's JSON escapes
    /// non-ASCII text (six bytes per Korean character), so the character cut alone cannot keep a page
    /// of long Korean alerts inside the 1 MB frame; half of it leaves room for the envelope.
    /// </summary>
    public const int MaxResponseBytes = 512 * 1024;

    /// <summary>
    /// Drops the oldest entries of <paramref name="page"/> until it serializes to at most
    /// <paramref name="maxBytes"/>; a trimmed page says <c>More</c>, so the rest comes with the next one.
    /// One entry is always kept (a single entry is far below the limit).
    /// </summary>
    public static HistoryPage FitToBytes(HistoryPage page, int maxBytes, JsonSerializerOptions json)
    {
        var entries = page.Entries.ToList();
        var trimmed = false;
        while (entries.Count > 1 && JsonSerializer.SerializeToUtf8Bytes(new HistoryPage(entries, page.More), json).Length > maxBytes)
        {
            entries.RemoveRange(entries.Count - Math.Max(1, entries.Count / 10), Math.Max(1, entries.Count / 10));
            trimmed = true;
        }
        return trimmed ? new HistoryPage(entries, More: true) : page;
    }

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

/// <summary>
/// Where the next page starts: just after this entry in the list's order (time, then kind, then id,
/// all descending), so entries sharing a timestamp across a page boundary are neither lost nor repeated.
/// </summary>
public sealed record HistoryCursor(DateTimeOffset At, string Kind, long Id)
{
    public static HistoryCursor After(HistoryEntry e) => new(e.At, e.Kind, e.Id);
}

/// <summary>
/// Which relayed notifications <c>history.clear</c> deletes: those of <c>App</c> (a received or sent file,
/// clipboard text, a command) and/or those sent by <c>Source</c> (the device name as sent). Both null: all.
/// </summary>
public sealed record HistoryClearScope(string? App = null, string? Source = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAll => App is null && Source is null;
}

public interface INotificationHistoryStore
{
    Task AddAsync(DeviceNotificationRecord record, CancellationToken ct);

    /// <summary>Entries after <paramref name="after"/> in list order (or from the newest), newest first.</summary>
    Task<HistoryPage> ListAsync(HistoryCursor? after, int limit, CancellationToken ct);

    /// <summary>
    /// Deletes relayed notifications: all of them, or only those of <paramref name="scope"/> (one app, one sender).
    /// The alert log stays (it is also diagnostics, and its rows hold back repeated alerts).
    /// </summary>
    Task<int> ClearDevicesAsync(HistoryClearScope? scope, CancellationToken ct);

    /// <summary>Deletes one relayed notification by its <see cref="HistoryEntry.Id"/>. False when it was not there.</summary>
    Task<bool> DeleteDeviceAsync(long id, CancellationToken ct);

    /// <summary>Drops relayed notifications and alert-log rows older than <paramref name="cutoff"/>, and relayed ones beyond <paramref name="maxDeviceEntries"/>.</summary>
    Task<int> PruneAsync(DateTimeOffset cutoff, int maxDeviceEntries, CancellationToken ct);
}
