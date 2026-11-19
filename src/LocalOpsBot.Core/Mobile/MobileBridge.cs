using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Tray ↔ Agent duplex pipe for the mobile feature (<c>\\.\pipe\Homebase.MobileBridge</c>).
/// A narrow precursor of the planned SessionBridge (Device Hub TRAIN-B): only the operations in
/// <see cref="AllowedTrayOps"/> are carried out for the Tray. A request with any other op gets a
/// failed response with <see cref="ErrorUnsupported"/> (see <see cref="UnsupportedResponse"/>) and the
/// connection stays open, so a newer Tray keeps working against an older Agent. Agents up to
/// v0.30.0 closed the connection instead.
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
    public const string OpHistoryList = "history.list";       // request {after?, limit?} → HistoryPage (M-32)
    public const string OpHistoryClear = "history.clear";     // request → int (relayed notifications removed; M-32)
    public const string OpAgentAlert = "agent.alert";         // push AgentAlert — an AI session waits or finished (M-45)
    public const string OpLiveGet = "live.get";               // request {target} → LiveView, keeps that view open ~10 s (M-46)
    public const string OpApprovalAsk = "approval.ask";       // request ApprovalRequest → ApprovalAskResult (M-48, waits up to 10 min)
    public const string OpApprovalCancel = "approval.cancel"; // push {id} — the user is back at the PC (M-48)
    public const string OpAliasesSet = "aliases.set";         // request AliasSetRequest → bool, shared with the group (M-51)
    public const string OpDevicesRefresh = "devices.refresh"; // request → DevicesRefreshResult: retry linked PCs now, group sync (M-59)
    public const string OpAdvise = "advise";                  // request → AdviceResponse, the local LLM's advice (M-60, takes up to a minute)
    public const string OpClipReady = "clip.ready";           // push — this Tray puts received clipboard text on the clipboard (M-62)
    public const string OpClipTargets = "clip.targets";       // request → ClipTargetList: where clipboard text can go (M-62)
    public const string OpClipSend = "clip.send";             // request ClipSendRequest → ClipSendResult (M-62, waits for every target)
    public const string OpClipDone = "clip.done";             // push TrayClipDone — whether clip.set {id} reached the clipboard (M-62)
    public const string OpFilesReady = "files.ready";         // push — this Tray saves received files; the Agent offers what waits (M-64)
    public const string OpTransferSaved = "transfer.saved";   // push TrayTransferSaved — the Tray saved transfer.ready {id} (or could not) (M-64)
    public const string OpFilesTargets = "files.targets";     // request → ClipTargetList: where files can go (M-65)
    public const string OpFilesBegin = "files.begin";         // request FilesBeginRequest → FilesBeginResult; then the files over Homebase.Transfer (M-65)
    public const string OpFilesGo = "files.go";               // request FilesIdRequest → bool: every file is in, send them (M-65)
    public const string OpFilesCancel = "files.cancel";       // request FilesIdRequest → bool (M-65)

    // Agent → Tray
    public const string OpWatch = "watch";                    // push {on, remote} — collect only while someone watches; remote: another device (M-46)
    public const string OpToast = "toast.show";               // push {title, body} — a relayed notification to show (M-28)
    public const string OpAliases = "aliases";                // push AliasListPayload — the group's aliases, on connect and on change (M-51)
    public const string OpClipSet = "clip.set";               // push TrayClip with an id — text another device sent; the Tray answers clip.done (M-62)
    public const string OpTransferReady = "transfer.ready";   // push TrayTransfer — files another device sent, to read from Homebase.Transfer and save (M-64)
    public const string OpFilesProgress = "files.progress";   // push FilesProgress — files this PC sends, per target (M-65)
    public const string OpResponse = "response";

    public static readonly IReadOnlySet<string> AllowedTrayOps = new HashSet<string>(StringComparer.Ordinal)
    {
        OpSessionReport, OpPairingBegin, OpDevicesList, OpDevicesRevoke, OpStatus, OpNotifyTest,
        OpDevicesOverview, OpHubsLink, OpHubsUnlink, OpHistoryList, OpHistoryClear, OpAgentAlert, OpLiveGet,
        OpApprovalAsk, OpApprovalCancel, OpAliasesSet, OpDevicesRefresh, OpAdvise,
        OpClipReady, OpClipTargets, OpClipSend, OpClipDone, OpFilesReady, OpTransferSaved,
        OpFilesTargets, OpFilesBegin, OpFilesGo, OpFilesCancel,
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

    /// <summary><c>error</c> of a response whose request op (or feature) this Agent does not support.</summary>
    public const string ErrorUnsupported = "unsupported";

    /// <summary>A string property of a frame, or <c>null</c> when it is missing or not a JSON string.</summary>
    public static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// The answer to a frame whose op is not supported: <c>{op:"response", id, ok:false, error:"unsupported"}</c>
    /// when the frame is a request (it carries a string <c>id</c>), else <c>null</c> — a push nobody waits on.
    /// </summary>
    public static JsonObject? UnsupportedResponse(JsonObject frame) =>
        StringOf(frame["id"]) is { } id ? Response(id, false, error: ErrorUnsupported) : null;
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

/// <summary>
/// <c>toast.show</c> payload: a notification relayed from another device (DECISIONS M-28). <c>Title</c> is
/// "sender · app"; <c>Source</c>/<c>SourceKind</c>/<c>App</c> (v0.38.0) name the sender apart, so the Tray can
/// show the alias the user gave it there instead (M-50). An older Agent leaves them out.
/// </summary>
public sealed record TrayToast(string Title, string? Body, string? Source = null, string? SourceKind = null, string? App = null);

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

/// <summary>
/// <c>devices.overview</c>: what is paired here, and the PCs this one is linked to. <c>Local</c> (v0.34.0)
/// adds what only this PC knows about its own devices: when they paired and whether they take push.
/// </summary>
public sealed record DeviceOverview(
    DevicesResponse Here,
    IReadOnlyList<LinkedHubView> Hubs,
    IReadOnlyList<LocalDeviceInfo>? Local = null);

/// <summary>
/// <c>devices.refresh</c> (M-59): the Tray's refresh asked every PC this one is linked to again at once.
/// <c>Pcs</c> is how many there are, <c>Reached</c> how many answered within the wait.
/// </summary>
public sealed record DevicesRefreshResult(int Pcs, int Reached);

/// <summary>
/// A device paired with this PC as the Tray's device page shows it (M-46): <c>Push</c> — it registered a
/// UnifiedPush endpoint here; <c>Muted</c> — the notification kinds it asked this PC not to send.
/// </summary>
public sealed record LocalDeviceInfo(string DeviceId, DateTimeOffset PairedAt, bool Push, IReadOnlyList<string>? Muted);

/// <summary>
/// What <c>live.get</c> looks at (M-46): this PC, a hub PC this one is linked to (its live WebSocket), or a
/// PC paired here as a device (it shares its snapshots on request).
/// </summary>
public static class LiveTarget
{
    public const string Self = "self";
    private const string HubPrefix = "hub:";
    private const string DevicePrefix = "device:";

    public static string Hub(string pcId) => HubPrefix + pcId;
    public static string Device(string deviceId) => DevicePrefix + deviceId;

    public static string? HubId(string target) =>
        target.StartsWith(HubPrefix, StringComparison.Ordinal) && target.Length > HubPrefix.Length ? target[HubPrefix.Length..] : null;

    public static string? DeviceId(string target) =>
        target.StartsWith(DevicePrefix, StringComparison.Ordinal) && target.Length > DevicePrefix.Length ? target[DevicePrefix.Length..] : null;
}

/// <summary>States of a <see cref="LiveView"/>.</summary>
public static class LiveViewState
{
    /// <summary><c>Snapshot</c> is current.</summary>
    public const string Live = "live";
    /// <summary>Asked for, nothing yet (connecting, or waiting for the other PC to start sharing).</summary>
    public const string Connecting = "connecting";
    /// <summary>The other PC does not answer; <c>Snapshot</c> may hold the last one seen.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>The other PC is too old to share its live view (before v0.34.0).</summary>
    public const string Unsupported = "unsupported";
    /// <summary>No such device or link here.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// <c>live.get</c> answer. <c>Via</c> names the path (<c>lan</c>, <c>tailscale</c>, <c>cloudflare</c>, or
/// <c>shared</c> when the other PC pushes to this one); <c>Detail</c> says why it is not live.
/// </summary>
public sealed record LiveView(string State, LiveSnapshot? Snapshot = null, string? Via = null, string? Detail = null, DateTimeOffset? At = null);

/// <summary><c>history.list</c> request: entries after <c>After</c> (or from the newest), at most <c>Limit</c> (M-32).</summary>
public sealed record HistoryListRequest(LocalOpsBot.Core.Notifications.HistoryCursor? After, int? Limit);

/// <summary><c>hubs.link</c> result. <c>Error</c> codes: <c>invalid_link</c>, <c>unreachable</c>, <c>rejected</c>, <c>self</c>, <c>off</c>.</summary>
public sealed record HubLinkResult(bool Linked, string? PcName, string? Error, string? Message);
