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
/// <c>POST /api/v1/presence</c> body (DECISIONS M-27, M-46): a device's own state. Phones fill battery,
/// network, memory, storage and the rest of what Android tells without extra permissions; PCs fill CPU,
/// memory, uptime and OS. <c>PushHashes</c> are the SHA-256 (lowercase hex) of the UnifiedPush endpoints
/// the device itself delivers to — a PC lists its phones so the hub does not push the same relayed
/// notification to them twice (M-28).
/// </summary>
/// <remarks>
/// The fields after <c>PushHashes</c> came with v0.34.0 (M-46). They are display-only: a value out of
/// range is dropped by <see cref="Sanitized"/> instead of failing the whole report, so one odd sensor on
/// a phone never hides the rest of its state. <c>Endpoints</c> (v0.45.0, M-59): a linked PC's own paths
/// right now, so a hub whose link back to it went stale can take them up (phones leave it out).
/// </remarks>
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
    IReadOnlyList<string>? PushHashes = null,
    string? Os = null,
    long? StorageUsedBytes = null,
    long? StorageTotalBytes = null,
    double? BatteryTempC = null,
    string? BatteryHealth = null,
    string? Plug = null,
    long? ChargeFullSec = null,
    bool? PowerSave = null,
    string? Thermal = null,
    int? Signal = null,
    int? LinkMbps = null,
    bool? Forwarding = null,
    bool? Dnd = null,
    string? PcId = null,
    IReadOnlyList<MobileEndpoint>? Endpoints = null)
{
    public const int MaxPushHashes = 32;

    /// <summary>More paths than any PC has (LAN adapters, Tailscale name and address, Cloudflare): the list is dropped.</summary>
    public const int MaxEndpoints = 16;

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

    /// <summary>The report with the v0.34.0 display fields that are out of range left out (M-46).</summary>
    public PresenceReport Sanitized() => this with
    {
        Os = Word(Os, 64),
        StorageUsedBytes = StorageUsedBytes is >= 0 && StorageTotalBytes is > 0 && StorageUsedBytes <= StorageTotalBytes ? StorageUsedBytes : null,
        StorageTotalBytes = StorageUsedBytes is >= 0 && StorageTotalBytes is > 0 && StorageUsedBytes <= StorageTotalBytes ? StorageTotalBytes : null,
        BatteryTempC = BatteryTempC is >= -40 and <= 120 ? Math.Round(BatteryTempC.Value, 1) : null,
        BatteryHealth = Word(BatteryHealth, 16),
        Plug = Word(Plug, 16),
        ChargeFullSec = ChargeFullSec is > 0 and <= 7 * 86400 ? ChargeFullSec : null,
        Thermal = Word(Thermal, 16),
        Signal = Signal is >= 0 and <= 4 ? Signal : null,
        LinkMbps = LinkMbps is > 0 and <= 100_000 ? LinkMbps : null,
        // A linked PC names itself so the group knows it (M-47); phones leave it out.
        PcId = Kind == DeviceKind.Pc ? Word(PcId, 64) : null,
        // …and its paths (M-59), which the hub checks one by one before it uses any (HubPaths).
        Endpoints = Kind == DeviceKind.Pc && Endpoints is { Count: > 0 and <= MaxEndpoints } e && e.All(x => x is not null) ? e : null,
    };

    private static string? Word(string? s, int max) => string.IsNullOrWhiteSpace(s) || s.Length > max ? null : s.Trim();

    private static bool IsHash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public DeviceStatus ToStatus() => new(AppVersion, Battery, Charging, Network, CpuPct, MemUsedBytes, MemTotalBytes, UptimeSec,
        Os, StorageUsedBytes, StorageTotalBytes, BatteryTempC, BatteryHealth, Plug, ChargeFullSec, PowerSave, Thermal, Signal,
        LinkMbps, Forwarding, Dnd);
}

/// <summary>
/// The last reported state of one device; every field may be missing. The fields after <c>UptimeSec</c>
/// are the v0.34.0 additions of <see cref="PresenceReport"/> (M-46); older devices and hubs leave them out.
/// </summary>
public sealed record DeviceStatus(
    string? AppVersion = null,
    int? Battery = null,
    bool? Charging = null,
    string? Network = null,
    double? CpuPct = null,
    long? MemUsedBytes = null,
    long? MemTotalBytes = null,
    long? UptimeSec = null,
    string? Os = null,
    long? StorageUsedBytes = null,
    long? StorageTotalBytes = null,
    double? BatteryTempC = null,
    string? BatteryHealth = null,
    string? Plug = null,
    long? ChargeFullSec = null,
    bool? PowerSave = null,
    string? Thermal = null,
    int? Signal = null,
    int? LinkMbps = null,
    bool? Forwarding = null,
    bool? Dnd = null);

/// <summary>
/// One device paired with a hub PC. <c>Online</c>: it reported within <see cref="DeviceStatusBoard.OnlineWindow"/>.
/// <c>Status</c> is null until its first report since the hub started. <c>PcId</c> (v0.38.0): a linked PC's
/// own id from its last report, so the group's alias for it can be found (M-51).
/// </summary>
public sealed record DeviceEntry(
    string DeviceId,
    string Name,
    string Kind,
    string? Model,
    DateTimeOffset? LastSeenAt,
    bool Online,
    DeviceStatus? Status,
    string? PcId = null);

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

/// <summary>
/// <c>Next</c> is the <c>after</c> value for the following poll. <c>Live</c> (v0.34.0, M-46) is true while
/// someone on the hub is looking at the polling PC: it should then share its live snapshots with
/// <c>POST /api/v1/live-snapshot</c> until the hub answers <c>live: false</c>.
/// </summary>
public sealed record InboxResponse(int Schema, long Next, IReadOnlyList<InboxItem> Items, bool? Live = null);

/// <summary>
/// <c>POST /api/v1/live-snapshot</c> answer (M-46): <c>Live</c> false means nobody on the hub is watching
/// any more, so the sharing PC stops.
/// </summary>
public sealed record LiveShareResponse(int Schema, bool Live);

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

    /// <summary>
    /// Drops the reports of devices no longer in <paramref name="pairedIds"/>: revoked in the Tray, or
    /// replaced by a re-pair of the same phone, so their last state does not linger in memory.
    /// </summary>
    public void Retain(IEnumerable<string> pairedIds)
    {
        var keep = pairedIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var gone in _reports.Keys.Where(id => !keep.Contains(id)).ToList())
                _reports.Remove(gone);
        }
    }

    /// <summary>How many devices have a report kept (for tests and diagnostics).</summary>
    public int Count
    {
        get { lock (_gate) return _reports.Count; }
    }

    /// <summary>The pcId a linked PC reported (M-47), or null.</summary>
    public string? PcIdOf(string deviceId)
    {
        lock (_gate) return _reports.TryGetValue(deviceId, out var r) ? r.Report.PcId : null;
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
                        has && now - r.At <= OnlineWindow, has ? r.Report.ToStatus() : null,
                        has && d.Kind == DeviceKind.Pc ? r.Report.PcId : null);
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
    void Show(TrayToast toast);
}
