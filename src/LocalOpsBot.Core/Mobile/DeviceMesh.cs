using System.Security.Cryptography;
using System.Text;

namespace LocalOpsBot.Core.Mobile;

/// <summary>What a paired device is (DECISIONS M-27).</summary>
public static class DeviceKind
{
    public const string Phone = "phone";
    public const string Pc = "pc";

    /// <summary>Anything but <c>pc</c> is a phone: older apps send no kind.</summary>
    public static string Normalize(string? kind) => string.Equals(kind, Pc, StringComparison.Ordinal) ? Pc : Phone;
}

/// <summary>
/// <c>POST /api/v1/presence</c> body (DECISIONS M-27): a device's own state. Phones fill battery and
/// network; PCs fill CPU, memory and uptime. <c>PushHashes</c> are the SHA-256 (lowercase hex) of the
/// UnifiedPush endpoints the device itself delivers to — a PC lists its phones so the hub does not
/// push the same relayed notification to them twice (M-28).
/// </summary>
public sealed record PresenceReport(
    int Schema,
    string Kind,
    string? AppVersion = null,
    int? Battery = null,
    bool? Charging = null,
    string? Network = null,
    double? CpuPct = null,
    long? MemUsedBytes = null,
    long? MemTotalBytes = null,
    long? UptimeSec = null,
    IReadOnlyList<string>? PushHashes = null)
{
    public const int MaxPushHashes = 32;

    /// <summary>Why the report is unusable, or <c>null</c>.</summary>
    public static string? Validate(PresenceReport? r)
    {
        if (r is null) return "Invalid presence report.";
        if (r.Schema != MobileProtocol.Schema) return $"Unsupported schema {r.Schema}.";
        if (r.Battery is < 0 or > 100) return "Battery must be 0-100.";
        if (r.CpuPct is < 0 or > 100) return "CPU must be 0-100.";
        if (r.MemUsedBytes is < 0 || r.MemTotalBytes is < 0 || r.UptimeSec is < 0) return "Negative counters.";
        if (r.AppVersion is { Length: > 32 } || r.Network is { Length: > 32 }) return "Field too long.";
        if (r.PushHashes is { } h && (h.Count > MaxPushHashes || !h.All(IsHash))) return "Push hashes are invalid.";
        return null;
    }

    private static bool IsHash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public DeviceStatus ToStatus() => new(AppVersion, Battery, Charging, Network, CpuPct, MemUsedBytes, MemTotalBytes, UptimeSec);
}

/// <summary>The last reported state of one device; every field may be missing.</summary>
public sealed record DeviceStatus(
    string? AppVersion = null,
    int? Battery = null,
    bool? Charging = null,
    string? Network = null,
    double? CpuPct = null,
    long? MemUsedBytes = null,
    long? MemTotalBytes = null,
    long? UptimeSec = null);

/// <summary>
/// One device paired with a hub PC. <c>Online</c>: it reported within <see cref="DeviceStatusBoard.OnlineWindow"/>.
/// <c>Status</c> is null until its first report since the hub started.
/// </summary>
public sealed record DeviceEntry(
    string DeviceId,
    string Name,
    string Kind,
    string? Model,
    DateTimeOffset? LastSeenAt,
    bool Online,
    DeviceStatus? Status);

/// <summary>The hub PC itself in <see cref="DevicesResponse"/>.</summary>
public sealed record HubEntry(string PcId, string PcName, string AgentVersion, DeviceStatus Status);

/// <summary>
/// <c>POST /api/v1/presence</c> and <c>GET /api/v1/devices</c> response. <c>PushHashes</c> are the
/// hub's own push targets, so a linked PC skips those phones when it relays the hub's notifications.
/// </summary>
public sealed record DevicesResponse(
    int Schema,
    HubEntry Hub,
    IReadOnlyList<DeviceEntry> Devices,
    IReadOnlyList<string>? PushHashes = null);

/// <summary><c>POST /api/v1/notifications</c> body: a notification that reached the sending device (M-28).</summary>
public sealed record DeviceNotification(int Schema, string Id, string App, string Title, string? Body, DateTimeOffset Ts)
{
    public const int MaxApp = 64;
    public const int MaxTitle = 200;
    public const int MaxBody = 1000;

    public static string? Validate(DeviceNotification? n)
    {
        if (n is null) return "Invalid notification.";
        if (n.Schema != MobileProtocol.Schema) return $"Unsupported schema {n.Schema}.";
        if (string.IsNullOrWhiteSpace(n.Id) || n.Id.Length > 64) return "Id is missing or too long.";
        if (string.IsNullOrWhiteSpace(n.App) || n.App.Length > MaxApp) return "App is missing or too long.";
        if (n.Title is null || n.Title.Length > MaxTitle) return "Title is missing or too long.";
        if (n.Body is { Length: > MaxBody }) return "Body is too long.";
        return null;
    }
}

/// <summary>One notification in a hub's inbox for linked PCs (<c>GET /api/v1/inbox</c>).</summary>
public sealed record InboxItem(
    long Seq,
    string Id,
    string OriginId,
    string OriginName,
    string App,
    string Title,
    string? Body,
    DateTimeOffset Ts);

/// <summary><c>Next</c> is the <c>after</c> value for the following poll.</summary>
public sealed record InboxResponse(int Schema, long Next, IReadOnlyList<InboxItem> Items);

/// <summary><c>DELETE /api/v1/device</c> response: the caller unpaired itself.</summary>
public sealed record UnpairResponse(int Schema, string DeviceId, bool Removed);

public static class PushHash
{
    /// <summary>SHA-256 of a push endpoint URL, lowercase hex — names a phone without revealing its endpoint.</summary>
    public static string Of(string endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant();
}

/// <summary>
/// Last presence report of every device, in memory only (DECISIONS M-27): a restart starts empty and
/// the next reports fill it, so a stale value never looks live.
/// </summary>
public sealed class DeviceStatusBoard
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time;
    private readonly Dictionary<string, (PresenceReport Report, DateTimeOffset At)> _reports = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public DeviceStatusBoard(TimeProvider time) => _time = time;

    public void Report(string deviceId, PresenceReport report)
    {
        lock (_gate) _reports[deviceId] = (report, _time.GetUtcNow());
    }

    public void Forget(string deviceId)
    {
        lock (_gate) _reports.Remove(deviceId);
    }

    /// <summary>Push hashes the device said it delivers to itself (empty when unknown).</summary>
    public IReadOnlyCollection<string> PushHashesOf(string deviceId)
    {
        lock (_gate)
            return _reports.TryGetValue(deviceId, out var r) && r.Report.PushHashes is { } h ? h.ToArray() : Array.Empty<string>();
    }

    /// <summary>The device list for <see cref="DevicesResponse"/>, PCs first, then by name.</summary>
    public IReadOnlyList<DeviceEntry> Entries(IEnumerable<MobileDeviceInfo> devices)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return devices.Select(d =>
                {
                    var has = _reports.TryGetValue(d.DeviceId, out var r);
                    var seen = has ? r.At : d.LastSeenAt;
                    return new DeviceEntry(d.DeviceId, d.Name, d.Kind, d.Model, seen,
                        has && now - r.At <= OnlineWindow, has ? r.Report.ToStatus() : null);
                })
                .OrderBy(e => e.Kind == DeviceKind.Pc ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}

/// <summary>
/// The notifications a hub hands to linked PCs (DECISIONS M-28): its own forwarded Windows
/// notifications and what its devices sent, newest <see cref="Capacity"/> within <see cref="Retention"/>.
/// <see cref="WaitAsync"/> is a long poll: it returns at once when there is something new.
/// </summary>
public sealed class NotificationInbox
{
    public const int Capacity = 200;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    /// <summary>Origin id of the hub's own notifications.</summary>
    public const string SelfOrigin = "self";

    private readonly TimeProvider _time;
    private readonly LinkedList<InboxItem> _items = new();
    private readonly object _gate = new();
    private long _seq;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NotificationInbox(TimeProvider time) => _time = time;

    public long LatestSeq { get { lock (_gate) return _seq; } }

    public void Add(string originId, string originName, string app, string title, string? body, DateTimeOffset ts, string? id = null)
    {
        TaskCompletionSource fire;
        lock (_gate)
        {
            _items.AddLast(new InboxItem(++_seq, id ?? Guid.NewGuid().ToString("N"), originId, originName, app, title, body, ts));
            while (_items.Count > Capacity) _items.RemoveFirst();
            fire = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fire.TrySetResult();
    }

    /// <summary>
    /// Items after <paramref name="after"/> not sent by <paramref name="requesterId"/>. A cursor ahead
    /// of the inbox (the hub restarted) starts over from the oldest item that is still kept.
    /// </summary>
    public InboxResponse Read(long after, string requesterId)
    {
        var cutoff = _time.GetUtcNow() - Retention;
        lock (_gate)
        {
            if (after > _seq) after = 0;
            var list = _items.Where(i => i.Seq > after && i.Ts >= cutoff && i.OriginId != requesterId).ToList();
            return new InboxResponse(MobileProtocol.Schema, _seq, list);
        }
    }

    public async Task<InboxResponse> WaitAsync(long after, string requesterId, TimeSpan timeout, CancellationToken ct)
    {
        Task changed;
        lock (_gate) changed = _changed.Task;
        var now = Read(after, requesterId);
        if (now.Items.Count > 0 || after > now.Next) return now;
        try { await changed.WaitAsync(timeout, _time, ct); }
        catch (TimeoutException) { }
        return Read(after, requesterId);
    }
}

/// <summary>Shows a Windows notification in the user's session through the Tray (DECISIONS M-28).</summary>
public interface ITrayToaster
{
    void Show(string title, string? body);
}
