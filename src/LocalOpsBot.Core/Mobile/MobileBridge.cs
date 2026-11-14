using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Tray ↔ Agent duplex pipe for the mobile feature (<c>\\.\pipe\Homebase.MobileBridge</c>).
/// A narrow precursor of the planned SessionBridge (Device Hub TRAIN-B): only the operations in
/// <see cref="AllowedTrayOps"/> are accepted from the Tray; everything else closes the connection.
/// </summary>
public static class MobileBridge
{
    public const string PipeName = "Homebase.MobileBridge";

    /// <summary>Control frames are tiny; anything larger is not ours.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    // Tray → Agent
    public const string OpSessionReport = "session.report";   // push: windows + claude sessions
    public const string OpPairingBegin = "pairing.begin";     // request → PairingOffer
    public const string OpDevicesList = "devices.list";       // request → MobileDeviceInfo[]
    public const string OpDevicesRevoke = "devices.revoke";   // request {deviceId} → bool
    public const string OpStatus = "status";                  // request → BridgeStatus
    public const string OpNotifyTest = "notify.test";         // request → NotifyTestResult (sent through the router)
    public const string OpDevicesOverview = "devices.overview"; // request → DeviceOverview (M-27, M-29)
    public const string OpHubsLink = "hubs.link";             // request {link} → HubLinkResult (M-29)
    public const string OpHubsUnlink = "hubs.unlink";         // request {pcId} → bool (M-29)

    // Agent → Tray
    public const string OpWatch = "watch";                    // push {on} — collect only while a phone watches
    public const string OpToast = "toast.show";               // push {title, body} — a relayed notification to show (M-28)
    public const string OpResponse = "response";

    public static readonly IReadOnlySet<string> AllowedTrayOps = new HashSet<string>(StringComparer.Ordinal)
    {
        OpSessionReport, OpPairingBegin, OpDevicesList, OpDevicesRevoke, OpStatus, OpNotifyTest,
        OpDevicesOverview, OpHubsLink, OpHubsUnlink,
    };

    /// <summary>Writes one frame: 4-byte little-endian length + UTF-8 JSON.</summary>
    public static async Task WriteFrameAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(MobileProtocol.Json));
        if (body.Length > MaxFrameBytes)
            throw new InvalidDataException($"Frame of {body.Length} bytes exceeds the {MaxFrameBytes} limit.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one frame; <c>null</c> on a clean end of stream. Throws on malformed frames.</summary>
    public static async Task<JsonObject?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var got = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException("Truncated frame header.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Invalid frame length {length}.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonNode.Parse(body) as JsonObject
               ?? throw new InvalidDataException("Frame is not a JSON object.");
    }

    public static JsonObject Message(string op, object? payload = null, string? id = null)
    {
        var msg = new JsonObject { ["op"] = op };
        if (id is not null) msg["id"] = id;
        if (payload is not null) msg["payload"] = JsonSerializer.SerializeToNode(payload, payload.GetType(), MobileProtocol.Json);
        return msg;
    }

    public static JsonObject Response(string id, bool ok, object? payload = null, string? error = null)
    {
        var msg = Message(OpResponse, payload, id);
        msg["ok"] = ok;
        if (error is not null) msg["error"] = error;
        return msg;
    }
}

/// <summary>
/// Outcome of the Tray's test notification. <c>Channel</c> is the channel that delivered it
/// (<c>unifiedpush</c>, <c>telegram</c>, …); <c>Error</c> is set when none did.
/// </summary>
public sealed record NotifyTestResult(bool Delivered, string? Channel, string? Error);

/// <summary>A visible top-level window as seen from the user's session.</summary>
public sealed record SessionWindow(long Hwnd, int Pid, string Title, bool Foreground);

/// <summary>
/// What the Tray reports while a phone is watching. <c>Agents</c> holds the AI coding sessions read
/// in the user's profile other than Claude Code (Codex; DECISIONS M-26); older Trays omit it.
/// </summary>
public sealed record SessionReport(
    IReadOnlyList<SessionWindow> Windows,
    IReadOnlyList<ClaudeSession> ClaudeSessions,
    IReadOnlyList<WorkItem>? Agents = null);

/// <summary>A paired device. <c>Kind</c> is <see cref="DeviceKind.Phone"/> or <see cref="DeviceKind.Pc"/> (DECISIONS M-27).</summary>
public sealed record MobileDeviceInfo(
    string DeviceId, string Name, string? Model, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, string Kind = DeviceKind.Phone);

/// <summary>
/// What the Tray's Settings › Phone tab shows. <c>Hosts</c> is the v1 Tailscale list;
/// <c>Endpoints</c> covers every path. <c>Cloudflare</c> is null while the tunnel is off.
/// </summary>
public sealed record BridgeStatus(
    bool ApiListening,
    IReadOnlyList<string> Hosts,
    int Port,
    int Watchers,
    IReadOnlyList<MobileEndpoint> Endpoints,
    MobileOptionsSummary Options,
    CloudflareStatus? Cloudflare);

/// <summary>Which paths are configured on (independent of whether each is currently reachable).</summary>
public sealed record MobileOptionsSummary(bool Tailscale, bool Lan, bool Cloudflare);

/// <summary>Cloudflare Tunnel connector state: <c>not_installed</c>, <c>starting</c>, <c>running</c>, <c>error</c>.</summary>
public sealed record CloudflareStatus(string State, string Mode, string? Hostname, string? Detail);

/// <summary><c>toast.show</c> payload: a notification relayed from another device (DECISIONS M-28).</summary>
public sealed record TrayToast(string Title, string? Body);

/// <summary>
/// A hub PC this PC is linked to, as the Tray shows it (DECISIONS M-29). <c>Devices</c> is the hub's
/// own device list from its last answer (this PC included, as <c>SelfDeviceId</c>); <c>Error</c> is the
/// last failure while <c>Online</c> is false.
/// </summary>
public sealed record LinkedHubView(
    string PcId,
    string PcName,
    bool Online,
    DateTimeOffset? LastContactAt,
    DeviceStatus? Status,
    IReadOnlyList<DeviceEntry> Devices,
    string? Error,
    string? SelfDeviceId = null);

/// <summary><c>devices.overview</c>: what is paired here, and the PCs this one is linked to.</summary>
public sealed record DeviceOverview(
    DevicesResponse Here,
    IReadOnlyList<LinkedHubView> Hubs);

/// <summary><c>hubs.link</c> result. <c>Error</c> codes: <c>invalid_link</c>, <c>unreachable</c>, <c>rejected</c>, <c>self</c>, <c>off</c>.</summary>
public sealed record HubLinkResult(bool Linked, string? PcName, string? Error, string? Message);
