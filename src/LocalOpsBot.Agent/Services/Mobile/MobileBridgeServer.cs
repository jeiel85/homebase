using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Localization;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Protocol.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Agent side of <c>\\.\pipe\Homebase.MobileBridge</c>. The Tray reports windows/Claude sessions
/// (only after the Agent says a phone is watching) and asks for pairing QR offers and the device
/// list. ACL: SYSTEM, Administrators and the configured user (else INTERACTIVE), the same as the
/// notification pipe (<see cref="TrayPipeSecurity"/>, Device Hub R-04). A request with an op this
/// Agent does not know is answered <c>unsupported</c> and the connection stays open (#45).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MobileBridgeServer : BackgroundService
{
    private const int MaxInstances = 4;
    private const int PipeBufferBytes = 64 * 1024;

    /// <summary>Distinct unsupported ops logged per connection; the rest are answered silently.</summary>
    private const int MaxUnsupportedOpsLogged = 16;

    private readonly LiveSnapshotHub _hub;
    private readonly MobileApiServer _api;
    private readonly IMobileDeviceStore _devices;
    private readonly IOutboundRouter? _router;
    private readonly DeviceHub? _deviceHub;
    private readonly HubLinkService? _hubLinks;
    private readonly TrayToastRelay? _toasts;
    private readonly INotificationHistoryStore? _history;
    private readonly TrayLiveService? _trayLive;
    private readonly DeviceGroupService? _group;
    private readonly MobileOptions _options;
    private readonly ILogger<MobileBridgeServer> _logger;
    private readonly object _gate = new();
    private readonly AgentAlertGate _agentAlerts = new();
    private readonly SemaphoreSlim _aliasSends = new(1, 1);
    private static readonly TimeSpan AliasSendTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the Tray's refresh waits for the linked PCs (a path that times out takes 5 s to connect).</summary>
    internal static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(8);
    private readonly ApprovalBroker? _approvals;
    private readonly OnDemandAdvice? _advice;
    private readonly ClipShareService? _clips;
    private readonly FileShareService? _files;
    private readonly FileSendService? _sends;
    private readonly List<Connection> _connections = new();

    /// <summary>
    /// How long the Tray's "send clipboard" may wait for its targets: a push takes up to 15 s, a PC up to 8 s per path
    /// (a PC with a LAN, a Tailscale and a Cloudflare path that all fail takes the longest).
    /// </summary>
    internal static readonly TimeSpan ClipSendWait = TimeSpan.FromSeconds(45);

    /// <summary>How long received text waits for a Tray to say it reached the clipboard (it retries a busy one briefly).</summary>
    internal static readonly TimeSpan ClipDeliverWait = TimeSpan.FromSeconds(10);

    /// <summary><c>clip.set</c> ids waiting for their Tray's <c>clip.done</c>.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _clipAcks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _commandAcks = new(StringComparer.Ordinal);
    private readonly RemoteCommandService? _commands;

    private sealed class Connection(Stream stream)
    {
        public Stream Stream { get; } = stream;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        /// <summary>Approvals this Tray waits on; withdrawn when it disconnects (#64).</summary>
        public ConcurrentDictionary<string, byte> Approvals { get; } = new(StringComparer.Ordinal);

        public volatile bool Closed;

        /// <summary>The Tray said it puts received clipboard text on the clipboard (M-62); an older one would drop it.</summary>
        public volatile bool ClipReady;

        /// <summary>The Tray said it saves received files (M-64).</summary>
        public volatile bool FilesReady;
        /// <summary>This Tray carries out remote commands (M-69); the custom ones its user registered.</summary>
        public volatile bool CommandsReady;
        public volatile IReadOnlyList<TrayCustomCommand> CustomCommands = Array.Empty<TrayCustomCommand>();

        /// <summary>Transfers handed to this Tray and not answered yet; offered again elsewhere if it goes away.</summary>
        public ConcurrentDictionary<string, byte> Transfers { get; } = new(StringComparer.Ordinal);
    }

    public MobileBridgeServer(
        LiveSnapshotHub hub,
        MobileApiServer api,
        IMobileDeviceStore devices,
        IOptions<MobileOptions> options,
        ILogger<MobileBridgeServer> logger,
        IOutboundRouter? router = null,
        DeviceHub? deviceHub = null,
        HubLinkService? hubLinks = null,
        TrayToastRelay? toasts = null,
        INotificationHistoryStore? history = null,
        TrayLiveService? trayLive = null,
        DeviceGroupService? group = null,
        ApprovalBroker? approvals = null,
        OnDemandAdvice? advice = null,
        ClipShareService? clips = null,
        FileShareService? files = null,
        FileSendService? sends = null,
        RemoteCommandService? commands = null)
    {
        _commands = commands;
        if (_commands is not null)
        {
            _commands.Tray = DeliverCommandAsync;
            _commands.Custom = ConnectedCustomCommands;
            _commands.Notice = OnCommandNotice;
        }
        _sends = sends;
        if (_sends is not null) _sends.Progress += OnFilesProgress;
        _files = files;
        if (_files is not null) _files.Tray = DeliverFiles;
        _clips = clips;
        if (_clips is not null) _clips.Tray = DeliverClipAsync;
        _advice = advice;
        _group = group;
        if (_group is not null) _group.Aliases.Changed += OnAliasesChanged;
        _approvals = approvals;
        _history = history;
        _trayLive = trayLive;
        _deviceHub = deviceHub;
        _hubLinks = hubLinks;
        _toasts = toasts;
        if (_toasts is not null) _toasts.Requested += OnToastRequested;
        _hub = hub;
        _api = api;
        _devices = devices;
        _router = router;
        _options = options.Value;
        _logger = logger;
        _hub.WatchingChanged += OnWatchingChanged;
        _hub.RemoteWatchingChanged += OnWatchingChanged;
    }

    /// <summary>
    /// <c>watch</c>: <c>on</c> — someone watches, so report windows; <c>remote</c> — it is another device,
    /// not this PC's own Tray, so show the "viewing this PC" indicator (M-46).
    /// </summary>
    private object WatchPayload() => new { on = _hub.IsWatching, remote = _hub.IsRemoteWatching };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    MobileBridge.PipeName, PipeDirection.InOut, MaxInstances,
                    // Non-zero buffers: with 0/0 a write blocks until the peer reads, so the Agent's
                    // initial "watch" frame and a client's first request deadlock each other.
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBufferBytes, PipeBufferBytes, BuildSecurity());
            }
            catch (IOException)
            {
                // All instances busy — wait for one to free up.
                await Task.Delay(1000, stoppingToken).ContinueWith(_ => { });
                running.RemoveAll(t => t.IsCompleted);
                continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Never let the bridge take the whole Agent (Telegram monitoring) down with it.
                _logger.LogError(ex, "Mobile bridge pipe could not be created; retrying in 30 s.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ContinueWith(_ => { });
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (IOException ex)
            {
                _logger.LogDebug("Mobile bridge client vanished before the handshake: {Message}", ex.Message);
                await pipe.DisposeAsync();
                continue;
            }

            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => ServeAsync(pipe, stoppingToken), CancellationToken.None));
        }
        await Task.WhenAll(running);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var conn = new Connection(pipe);
        var unsupportedLogged = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate) _connections.Add(conn);
        try
        {
            await SendAsync(conn, MobileBridge.Message(MobileBridge.OpWatch, WatchPayload()), ct);
            // In turn with the change broadcasts, so a change racing this connect can't leave the tray an older table.
            if (_group is not null) await SendAliasesAsync(new[] { conn }, ct);
            while (!ct.IsCancellationRequested)
            {
                var msg = await MobileBridge.ReadFrameAsync(pipe, ct);
                if (msg is null) break;
                var op = MobileBridge.StringOf(msg["op"]);
                if (op is null || !MobileBridge.AllowedTrayOps.Contains(op))
                {
                    // A newer Tray (installing, or a dev build) may send ops this Agent doesn't know.
                    // Dropping the connection made the Tray reconnect in a loop (#45): answer instead.
                    if (unsupportedLogged.Count < MaxUnsupportedOpsLogged && unsupportedLogged.Add(op ?? ""))
                        _logger.LogWarning("Mobile bridge: unsupported op '{Op}'; answered '{Error}'.", op, MobileBridge.ErrorUnsupported);
                    if (MobileBridge.UnsupportedResponse(msg) is { } reply) await SendAsync(conn, reply, ct);
                    continue;
                }
                await HandleAsync(conn, op, MobileBridge.StringOf(msg["id"]), msg["payload"], ct);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug("Mobile bridge connection ended: {Message}", ex.Message);
        }
        finally
        {
            lock (_gate) _connections.Remove(conn);
            // A Tray that went away can no longer hand an answer to its hook, and the hook already
            // fell back to the PC: withdraw its prompts now rather than at their deadline (#64).
            conn.Closed = true;
            foreach (var approvalId in conn.Approvals.Keys) _approvals?.Cancel(approvalId);
            // Files this Tray was saving when it went: hand them to another Tray now, or to the next one that connects.
            if (_files is not null && !conn.Transfers.IsEmpty)
            {
                foreach (var transferId in conn.Transfers.Keys) _files.OfferLost(transferId);
                _ = Task.Run(_files.OfferWaiting, CancellationToken.None);
            }
            await pipe.DisposeAsync();
        }
    }

    private async Task HandleAsync(Connection conn, string op, string? id, JsonNode? payload, CancellationToken ct)
    {
        if (op == MobileBridge.OpSessionReport)
        {
            var report = payload?.Deserialize<SessionReport>(MobileProtocol.Json);
            if (report is not null) _hub.UpdateSession(report);
            return;
        }
        if (op == MobileBridge.OpAgentAlert)
        {
            // A push takes seconds; sent off the read loop so this connection's session reports keep flowing (#61).
            _ = Task.Run(() => SendAgentAlertAsync(payload, ct), CancellationToken.None);
            return;
        }
        if (op == MobileBridge.OpApprovalCancel)
        {
            _approvals?.Cancel(MobileBridge.StringOf(payload?["id"]));
            return;
        }
        if (op == MobileBridge.OpClipReady)
        {
            conn.ClipReady = true;
            return;
        }
        if (op == MobileBridge.OpFilesReady)
        {
            conn.FilesReady = true;
            // Off the read loop: offering writes to this very connection.
            _ = Task.Run(() => _files?.OfferWaiting(), CancellationToken.None);
            return;
        }
        if (op == MobileBridge.OpTransferSaved)
        {
            TrayTransferSaved? saved;
            try { saved = payload?.Deserialize<TrayTransferSaved>(MobileProtocol.Json); }
            catch (JsonException) { saved = null; }
            if (saved is not null) conn.Transfers.TryRemove(saved.Id, out _);
            _files?.Saved(saved);
            return;
        }
        if (op == MobileBridge.OpCommandsReady)
        {
            TrayCustomCommands? custom;
            try { custom = payload?.Deserialize<TrayCustomCommands>(MobileProtocol.Json); }
            catch (JsonException) { custom = null; }
            conn.CustomCommands = custom?.Commands?.Where(c => c is { Id.Length: > 0, Name.Length: > 0 }).Take(20).ToList()
                ?? (IReadOnlyList<TrayCustomCommand>)Array.Empty<TrayCustomCommand>();
            conn.CommandsReady = true;
            return;
        }
        if (op == MobileBridge.OpCommandDone)
        {
            TrayCommandDone? done;
            try { done = payload?.Deserialize<TrayCommandDone>(MobileProtocol.Json); }
            catch (JsonException) { done = null; }
            if (done is { Id.Length: > 0 } && _commandAcks.TryGetValue(done.Id, out var ack)) ack.TrySetResult(done.Ok);
            return;
        }
        if (op == MobileBridge.OpClipDone)
        {
            TrayClipDone? done;
            try { done = payload?.Deserialize<TrayClipDone>(MobileProtocol.Json); }
            catch (JsonException) { done = null; }
            if (done is { Id.Length: > 0 } && _clipAcks.TryGetValue(done.Id, out var ack)) ack.TrySetResult(done.Ok);
            return;
        }
        if (op == MobileBridge.OpApprovalAsk && id is not null)
        {
            // Waits for a phone for up to 10 minutes: answered off the read loop so this connection's
            // session reports and other requests keep flowing meanwhile.
            _ = Task.Run(() => AskApprovalAsync(conn, id, payload, ct), CancellationToken.None);
            return;
        }

        if (id is null) return; // requests need an id to answer
        if (op == MobileBridge.OpDevicesRefresh)
        {
            // Waits a few seconds for the linked PCs: off the read loop so live views keep flowing (M-59).
            _ = Task.Run(() => RefreshDevicesAsync(conn, id, ct), CancellationToken.None);
            return;
        }
        if (op == MobileBridge.OpClipSend)
        {
            // Waits for pushes and other PCs: off the read loop so live views keep flowing (M-62).
            _ = Task.Run(() => SendClipAsync(conn, id, payload, ct), CancellationToken.None);
            return;
        }
        if (op is MobileBridge.OpFilesTargets or MobileBridge.OpFilesBegin or MobileBridge.OpFilesGo or MobileBridge.OpFilesCancel)
        {
            await SendAsync(conn, FilesRequest(op, id, payload), ct);
            return;
        }
        if (op == MobileBridge.OpCommandCancel)
        {
            // [Cancel] on the restart countdown toast or the tray menu (M-69).
            await SendAsync(conn, MobileBridge.Response(id, true, _commands?.CancelFromTray() == true), ct);
            return;
        }
        if (op == MobileBridge.OpClipTargets)
        {
            await SendAsync(conn, _clips is null
                ? MobileBridge.Response(id, false, error: MobileBridge.ErrorUnsupported)
                : MobileBridge.Response(id, true, new ClipTargetList(_clips.Targets())), ct);
            return;
        }
        if (op == MobileBridge.OpAdvise)
        {
            // Generating takes seconds to a minute: answered off the read loop so this connection's
            // session reports and other requests keep flowing meanwhile (M-60).
            _ = Task.Run(() => AdviseAsync(conn, id, ct), CancellationToken.None);
            return;
        }
        if (op == MobileBridge.OpNotifyTest)
        {
            await SendAsync(conn, MobileBridge.Response(id, true, await SendTestNotificationAsync(ct)), ct);
            return;
        }
        if (op == MobileBridge.OpHubsLink)
        {
            var link = (string?)payload?["link"] ?? "";
            var result = _hubLinks is null
                ? new HubLinkResult(false, null, "off", "휴대폰 연결 기능이 꺼져 있습니다.")
                : await _hubLinks.LinkAsync(link, ct);
            await SendAsync(conn, MobileBridge.Response(id, true, result), ct);
            return;
        }
        if (op is MobileBridge.OpHistoryList or MobileBridge.OpHistoryClear)
        {
            await SendAsync(conn, await HistoryAsync(op, id, payload, ct), ct);
            return;
        }
        if (op == MobileBridge.OpLiveGet)
        {
            var view = _trayLive?.Get(MobileBridge.StringOf(payload?["target"])) ?? new LiveView(LiveViewState.Unknown);
            await SendAsync(conn, MobileBridge.Response(id, true, view), ct);
            return;
        }
        if (op == MobileBridge.OpHubsUnlink)
        {
            // Unlinking takes the PC out of the whole group (M-47), not just this pair.
            var pcId = (string?)payload?["pcId"] ?? "";
            if (_group is not null && pcId.Length > 0) await _group.RemoveAsync(GroupKeys.Pc(pcId), fanOut: true, ct);
            var ok = _hubLinks is not null && (await _hubLinks.UnlinkAsync(pcId) || _group is not null);
            await SendAsync(conn, MobileBridge.Response(id, ok), ct);
            return;
        }
        if (op == MobileBridge.OpAliasesSet)
        {
            // The user named a device in the tray (M-51): shared with the whole group.
            var req = payload?.Deserialize<AliasSetRequest>(MobileProtocol.Json);
            if (_group is null || req is not { Key.Length: > 0, Name.Length: > 0 } || !GroupKeys.IsValid(req.Key))
            {
                await SendAsync(conn, MobileBridge.Response(id, false, error: _group is null ? "unsupported" : "bad_request"), ct);
                return;
            }
            _group.SetAlias(req.Key, req.Name, req.Alias);
            await SendAsync(conn, MobileBridge.Response(id, true), ct);
            return;
        }
        if (op == MobileBridge.OpDevicesRevoke && _group is not null)
        {
            // Removing a device takes it out of the whole group (M-47): every member revokes it.
            var deviceId = (string?)payload?["deviceId"] ?? "";
            // A phone known only by name and model, with another of that name here, goes from this PC alone (M-71).
            if (_devices.List().FirstOrDefault(d => d.DeviceId == deviceId) is { } device && _group.RemovalKeyOf(device) is { } key)
                await _group.RemoveAsync(key, fanOut: true, ct);
            var revoked = _devices.Revoke(deviceId) || !_devices.IsPaired(deviceId);
            await SendAsync(conn, MobileBridge.Response(id, revoked), ct);
            return;
        }
        JsonObject response = op switch
        {
            MobileBridge.OpPairingBegin => _api.CreateOffer() is { } offer
                ? MobileBridge.Response(id, true, offer)
                : MobileBridge.Response(id, false, error: "not_listening"),
            MobileBridge.OpDevicesList => MobileBridge.Response(id, true, _devices.List()),
            MobileBridge.OpDevicesRevoke => MobileBridge.Response(id,
                _devices.Revoke((string?)payload?["deviceId"] ?? "")),
            MobileBridge.OpStatus => MobileBridge.Response(id, true,
                _api.Status() with { DevicesVersion = PhoneReinstalls.ListVersion(_devices.List()) }),
            MobileBridge.OpDevicesOverview => _deviceHub is null
                ? MobileBridge.Response(id, false, error: "unsupported")
                : MobileBridge.Response(id, true, new DeviceOverview(_deviceHub.Devices(),
                    _hubLinks?.Views() ?? (IReadOnlyList<LinkedHubView>)Array.Empty<LinkedHubView>(), _deviceHub.LocalInfo(),
                    PhoneReinstalls.Superseded(_devices).Count)),
            _ => MobileBridge.Response(id, false, error: "unsupported"),
        };
        await SendAsync(conn, response, ct);
    }

    /// <summary>The Tray's notification center (DECISIONS M-32).</summary>
    private async Task<JsonObject> HistoryAsync(string op, string id, JsonNode? payload, CancellationToken ct)
    {
        if (_history is null) return MobileBridge.Response(id, false, error: "unsupported");
        try
        {
            if (op == MobileBridge.OpHistoryClear)
            {
                var removed = await _history.ClearDevicesAsync(ct);
                _logger.LogInformation("Notification center cleared from the Tray ({Count} relayed notifications)", removed);
                return MobileBridge.Response(id, true, removed);
            }
            var request = payload?.Deserialize<HistoryListRequest>(MobileProtocol.Json);
            var page = await _history.ListAsync(request?.After, NotificationHistoryRules.ClampLimit(request?.Limit), ct);
            return MobileBridge.Response(id, true,
                NotificationHistoryRules.FitToBytes(page, NotificationHistoryRules.MaxResponseBytes, MobileProtocol.Json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Notification center request {Op} failed", op);
            return MobileBridge.Response(id, false, error: "store_failed");
        }
    }

    /// <summary>
    /// An AI session the Tray saw start waiting or finish while the user was away (DECISIONS M-45):
    /// checked and rate-limited by <see cref="AgentAlertGate"/>, then pushed on encrypted channels.
    /// </summary>
    private async Task SendAgentAlertAsync(JsonNode? payload, CancellationToken ct)
    {
        if (_router is null) return;
        AgentAlert? alert;
        try { alert = AgentAlertGate.Sanitize(payload?.Deserialize<AgentAlert>(MobileProtocol.Json)); }
        catch (JsonException) { alert = null; }
        if (alert is null) return;
        if (alert.Kind == AgentAlertKind.Waiting && _approvals?.RecentlyAsked(alert.App, alert.Title) == true)
        {
            _logger.LogDebug("AI session waiting alert held back: its approval was just pushed");
            return;
        }
        var now = DateTimeOffset.UtcNow;
        bool pass;
        lock (_agentAlerts) pass = _agentAlerts.TryPass(alert, now);
        if (!pass)
        {
            _logger.LogDebug("AI session alert {Kind} held back by the rate limit", alert.Kind);
            return;
        }
        try
        {
            var result = await _router.DeliverAsync(AgentAlertGate.ToNotification(alert, now), ct);
            _logger.LogInformation("AI session alert {Kind} ({App}): delivered={Delivered}", alert.Kind, alert.App, result.Delivered);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI session alert {Kind} ({App}) could not be sent", alert.Kind, alert.App);
        }
    }

    /// <summary>
    /// The Tray's refresh (M-59): the group syncs now (its backoffs cleared) and every linked PC is tried at
    /// once; answered when they have all answered or after <see cref="RefreshWait"/>.
    /// </summary>
    private async Task RefreshDevicesAsync(Connection conn, string id, CancellationToken ct)
    {
        var result = new DevicesRefreshResult(0, 0);
        try
        {
            _group?.Refresh();
            if (_hubLinks is not null) result = await _hubLinks.RetryNowAsync(RefreshWait, ct);
            _logger.LogInformation("Devices refreshed from the Tray: {Reached} of {Pcs} linked PC(s) answered.", result.Reached, result.Pcs);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing the devices failed");
        }
        try { await SendAsync(conn, MobileBridge.Response(id, true, result), ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>A Claude Code permission prompt for the phones (M-48); answers null (ask on the PC) on any failure.</summary>
    /// <summary>The tray's "get advice now" (M-60): the same shared generation the phone app asks for.</summary>
    private async Task AdviseAsync(Connection conn, string id, CancellationToken ct)
    {
        JsonObject response;
        if (_advice is null)
        {
            response = MobileBridge.Response(id, false, error: MobileBridge.ErrorUnsupported);
        }
        else
        {
            try
            {
                var reply = await _advice.GetAsync(ct);
                _logger.LogInformation("AI advice for the tray: {Outcome}{Cached}.", reply.Ok ? "ok" : reply.Code, reply.Cached ? " (reused)" : "");
                response = MobileBridge.Response(id, true, AdviceResponse.From(reply));
            }
            catch (OperationCanceledException) { return; }
        }
        if (conn.Closed) return;
        try { await SendAsync(conn, response, ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The tray went away while the model was writing: nobody to answer.
        }
    }

    private async Task AskApprovalAsync(Connection conn, string id, JsonNode? payload, CancellationToken ct)
    {
        string? decision = null;
        string? requestId = null;
        try
        {
            var request = payload?.Deserialize<ApprovalRequest>(MobileProtocol.Json);
            if (_approvals is not null && request is not null && !string.IsNullOrEmpty(request.Id))
            {
                requestId = request.Id;
                conn.Approvals[requestId] = 0;
                if (conn.Closed) return; // the Tray left before the prompt went out
                decision = await _approvals.AskAsync(request, ct);
            }
        }
        catch (JsonException) { }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude approval failed; the PC asks as usual");
        }
        finally
        {
            if (requestId is not null) conn.Approvals.TryRemove(requestId, out _);
        }
        try { await SendAsync(conn, MobileBridge.Response(id, true, new ApprovalAskResult(decision)), ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>The Tray's "send test" goes through the same router as real alerts (push first, Telegram fallback).</summary>
    private async Task<NotifyTestResult> SendTestNotificationAsync(CancellationToken ct)
    {
        if (_router is null) return new NotifyTestResult(false, null, "No notification router.");
        var now = DateTimeOffset.UtcNow;
        var result = await _router.DeliverAsync(new OutboundNotification(
            Guid.NewGuid(), "test", OutboundPriority.Info,
            Strings.TestNotificationTitle(Environment.MachineName), Strings.TestNotificationBody,
            $"test:{now.ToUnixTimeMilliseconds()}", MessageSensitivity.Normal,
            DeliveryPolicy.TelegramFallback, now), ct);
        var via = result.Attempts.FirstOrDefault(a => a.Success)?.ChannelId;
        _logger.LogInformation("Test notification from the Tray: delivered={Delivered} via {Channel}", result.Delivered, via ?? "-");
        return new NotifyTestResult(result.Delivered, via, result.Delivered ? null : result.CombinedError);
    }

    /// <summary>The Tray's "send clipboard" (M-62): to phones by push, to linked PCs over the link.</summary>
    private async Task SendClipAsync(Connection conn, string id, JsonNode? payload, CancellationToken ct)
    {
        JsonObject response;
        if (_clips is null)
        {
            response = MobileBridge.Response(id, false, error: MobileBridge.ErrorUnsupported);
        }
        else
        {
            ClipSendRequest? request;
            try { request = payload?.Deserialize<ClipSendRequest>(MobileProtocol.Json); }
            catch (JsonException) { request = null; }
            if (request is null)
            {
                response = MobileBridge.Response(id, false, error: "bad_request");
            }
            else
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(ClipSendWait);
                try { response = MobileBridge.Response(id, true, await _clips.SendAsync(request, limit.Token)); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    response = MobileBridge.Response(id, false, error: "timeout");
                }
                catch (OperationCanceledException) { return; }
            }
        }
        if (conn.Closed) return;
        try { await SendAsync(conn, response, ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>
    /// Text another device sent (M-62) goes to every connected Tray that takes it — normally the one signed-in
    /// user's — and each says whether it reached the clipboard (<c>clip.done</c>). Null when no Tray takes it
    /// (nobody signed in); true once one put it there; false when none could or none answered in time.
    /// </summary>
    private async Task<bool?> DeliverClipAsync(TrayClip clip, CancellationToken ct)
    {
        List<Connection> conns;
        lock (_gate) conns = _connections.Where(c => c.ClipReady && !c.Closed).ToList();
        if (conns.Count == 0) return null;

        var acks = new List<(string Id, TaskCompletionSource<bool> Ack)>();
        foreach (var c in conns)
        {
            var id = Guid.NewGuid().ToString("N");
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _clipAcks[id] = ack;
            acks.Add((id, ack));
            try { await SendAsync(c, MobileBridge.Message(MobileBridge.OpClipSet, clip, id), ct); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
            {
                _logger.LogDebug("Clipboard text to Tray failed: {Message}", ex.Message);
                ack.TrySetResult(false);
            }
        }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ClipDeliverWait);
            var pending = acks.Select(a => a.Ack.Task).ToList();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(limit.Token);
                if (await finished) return true;
                pending.Remove(finished);
            }
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // no Tray answered in time
        }
        finally
        {
            foreach (var (id, _) in acks) _clipAcks.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// A remote command (M-69) for the signed-in user's Tray: sent to every Tray that runs them (for a custom one, those that
    /// registered it); true once one carried it out. Null when no Tray takes it (nobody signed in).
    /// </summary>
    private async Task<bool?> DeliverCommandAsync(TrayCommand command, CancellationToken ct)
    {
        List<Connection> conns;
        lock (_gate)
            conns = _connections.Where(c => c.CommandsReady && !c.Closed
                && (!RemoteCommandNames.IsCustom(command.Name)
                    || c.CustomCommands.Any(x => RemoteCommandNames.CustomPrefix + x.Id == command.Name))).ToList();
        if (conns.Count == 0) return null;

        var acks = new List<(string Id, TaskCompletionSource<bool> Ack)>();
        foreach (var c in conns)
        {
            var id = Guid.NewGuid().ToString("N");
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commandAcks[id] = ack;
            acks.Add((id, ack));
            try { await SendAsync(c, MobileBridge.Message(MobileBridge.OpCommandRun, command with { Id = id }, id), ct); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
            {
                _logger.LogDebug("Remote command to Tray failed: {Message}", ex.Message);
                ack.TrySetResult(false);
            }
        }
        try
        {
            var pending = acks.Select(a => a.Ack.Task).ToList();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(ct);
                if (await finished) return true;
                pending.Remove(finished);
            }
            return false;
        }
        finally
        {
            foreach (var (id, _) in acks) _commandAcks.TryRemove(id, out _);
        }
    }

    private IReadOnlyList<TrayCustomCommand> ConnectedCustomCommands()
    {
        lock (_gate)
            return _connections.Where(c => c.CommandsReady && !c.Closed).SelectMany(c => c.CustomCommands)
                .GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    /// <summary>A restart scheduled (with its time) or called off: every Tray shows it (M-69).</summary>
    private void OnCommandNotice(string name, DateTimeOffset? at, string by)
    {
        List<Connection> conns;
        lock (_gate) conns = _connections.Where(c => c.CommandsReady && !c.Closed).ToList();
        var message = MobileBridge.Message(MobileBridge.OpCommandNotice, new JsonObject
        {
            ["name"] = name,
            ["at"] = at?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["by"] = by,
        });
        _ = Task.Run(async () =>
        {
            foreach (var c in conns)
            {
                try { await SendAsync(c, message, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        });
    }

    /// <summary>The Tray's files.* requests (M-65): where files can go, room for them, send, cancel.</summary>
    private JsonObject FilesRequest(string op, string id, JsonNode? payload)
    {
        if (_sends is null) return MobileBridge.Response(id, false, error: MobileBridge.ErrorUnsupported);
        try
        {
            return op switch
            {
                MobileBridge.OpFilesTargets => MobileBridge.Response(id, true, new ClipTargetList(_sends.Targets())),
                MobileBridge.OpFilesBegin => MobileBridge.Response(id, true, _sends.Begin(payload?.Deserialize<FilesBeginRequest>(MobileProtocol.Json))),
                MobileBridge.OpFilesGo => MobileBridge.Response(id, _sends.Go(payload?.Deserialize<FilesIdRequest>(MobileProtocol.Json)?.Id)),
                _ => MobileBridge.Response(id, _sends.Cancel(payload?.Deserialize<FilesIdRequest>(MobileProtocol.Json)?.Id)),
            };
        }
        catch (JsonException)
        {
            return MobileBridge.Response(id, false, error: "bad_request");
        }
    }

    /// <summary>Where files this PC sends stand (M-65), to every Tray that takes files.</summary>
    private void OnFilesProgress(FilesProgress progress)
    {
        List<Connection> conns;
        lock (_gate) conns = _connections.Where(c => c.FilesReady && !c.Closed).ToList();
        foreach (var c in conns)
        {
            _ = SendAsync(c, MobileBridge.Message(MobileBridge.OpFilesProgress, progress), CancellationToken.None)
                .ContinueWith(t => _logger.LogDebug("Files progress to Tray failed: {Message}", t.Exception?.GetBaseException().Message),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>
    /// Finished files (M-64) go to one connected Tray that saves files — normally the signed-in user's. False when there
    /// is none: they wait (24 h) and are offered when one connects.
    /// </summary>
    private bool DeliverFiles(TrayTransfer transfer)
    {
        Connection? conn;
        lock (_gate) conn = _connections.FirstOrDefault(c => c.FilesReady && !c.Closed);
        if (conn is null) return false;
        conn.Transfers[transfer.Id] = 0;
        _ = SendAsync(conn, MobileBridge.Message(MobileBridge.OpTransferReady, transfer), CancellationToken.None)
            .ContinueWith(t => _logger.LogDebug("Files to Tray failed: {Message}", t.Exception?.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }

    private void OnToastRequested(TrayToast toast)
    {
        List<Connection> conns;
        lock (_gate) conns = _connections.ToList();
        foreach (var c in conns)
        {
            _ = SendAsync(c, MobileBridge.Message(MobileBridge.OpToast, toast), CancellationToken.None)
                .ContinueWith(t => _logger.LogDebug("Toast to Tray failed: {Message}", t.Exception?.GetBaseException().Message),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>Every tray gets the group's aliases again when they change (set here, by a phone, or by a sync).</summary>
    private void OnAliasesChanged()
    {
        List<Connection> conns;
        lock (_gate) conns = _connections.ToList();
        _ = SendAliasesAsync(conns, CancellationToken.None);
    }

    /// <summary>
    /// Sends the group's alias table to <paramref name="conns"/>. The tray replaces its whole table with each
    /// one, so sends run one at a time and each takes its snapshot only once it is its turn: a later send
    /// always carries a table at least as new as an earlier one, whatever thread raised the change (Codex
    /// review on #65). A send that can't finish within 5 s (a tray not reading) is dropped for that tray.
    /// </summary>
    private async Task SendAliasesAsync(IReadOnlyList<Connection> conns, CancellationToken ct)
    {
        try
        {
            await _aliasSends.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            var message = MobileBridge.Message(MobileBridge.OpAliases,
                new AliasListPayload(_group?.Aliases.List() ?? Array.Empty<DeviceAlias>()));
            foreach (var c in conns)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(AliasSendTimeout);
                try { await SendAsync(c, message, limit.Token); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    _logger.LogDebug("Aliases to Tray failed: {Message}", ex.Message);
                }
            }
        }
        finally
        {
            _aliasSends.Release();
        }
    }

    private void OnWatchingChanged(bool changed)
    {
        List<Connection> conns;
        lock (_gate) conns = _connections.ToList();
        var payload = WatchPayload();
        foreach (var c in conns)
        {
            _ = SendAsync(c, MobileBridge.Message(MobileBridge.OpWatch, payload), CancellationToken.None)
                .ContinueWith(t => _logger.LogDebug("Watch notify failed: {Message}", t.Exception?.GetBaseException().Message),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static async Task SendAsync(Connection conn, JsonObject message, CancellationToken ct)
    {
        await conn.WriteLock.WaitAsync(ct);
        try { await MobileBridge.WriteFrameAsync(conn.Stream, message, ct); }
        finally { conn.WriteLock.Release(); }
    }

    private PipeSecurity BuildSecurity() => TrayPipeSecurity.Build(_options.UserSid, _logger);

    public override void Dispose()
    {
        _hub.WatchingChanged -= OnWatchingChanged;
        _hub.RemoteWatchingChanged -= OnWatchingChanged;
        if (_toasts is not null) _toasts.Requested -= OnToastRequested;
        if (_clips is not null) _clips.Tray = null;
        if (_files is not null) _files.Tray = null;
        if (_commands is not null)
        {
            _commands.Tray = null;
            _commands.Custom = null;
            _commands.Notice = null;
        }
        if (_sends is not null) _sends.Progress -= OnFilesProgress;
        base.Dispose();
    }
}
