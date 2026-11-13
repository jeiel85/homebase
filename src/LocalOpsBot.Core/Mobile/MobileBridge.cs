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

    // Agent → Tray
    public const string OpWatch = "watch";                    // push {on} — collect only while a phone watches
    public const string OpResponse = "response";

    public static readonly IReadOnlySet<string> AllowedTrayOps = new HashSet<string>(StringComparer.Ordinal)
    {
        OpSessionReport, OpPairingBegin, OpDevicesList, OpDevicesRevoke, OpStatus, OpNotifyTest,
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

/// <summary>What the Tray reports while a phone is watching.</summary>
public sealed record SessionReport(
    IReadOnlyList<SessionWindow> Windows,
    IReadOnlyList<ClaudeSession> ClaudeSessions);

public sealed record MobileDeviceInfo(string DeviceId, string Name, string? Model, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt);

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
