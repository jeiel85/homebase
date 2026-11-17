using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly ApprovalBroker? _approvals;
    private readonly List<Connection> _connections = new();

    private sealed class Connection(Stream stream)
    {
        public Stream Stream { get; } = stream;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        /// <summary>Approvals this Tray waits on; withdrawn when it disconnects (#64).</summary>
        public ConcurrentDictionary<string, byte> Approvals { get; } = new(StringComparer.Ordinal);

        public volatile bool Closed;
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
        ApprovalBroker? approvals = null)
    {
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
        if (op == MobileBridge.OpApprovalAsk && id is not null)
        {
            // Waits for a phone for up to 10 minutes: answered off the read loop so this connection's
            // session reports and other requests keep flowing meanwhile.
            _ = Task.Run(() => AskApprovalAsync(conn, id, payload, ct), CancellationToken.None);
            return;
        }

        if (id is null) return; // requests need an id to answer
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
            if (_devices.List().FirstOrDefault(d => d.DeviceId == deviceId) is { } device && _group.KeyOf(device) is { } key)
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
            MobileBridge.OpStatus => MobileBridge.Response(id, true, _api.Status()),
            MobileBridge.OpDevicesOverview => _deviceHub is null
                ? MobileBridge.Response(id, false, error: "unsupported")
                : MobileBridge.Response(id, true, new DeviceOverview(_deviceHub.Devices(),
                    _hubLinks?.Views() ?? (IReadOnlyList<LinkedHubView>)Array.Empty<LinkedHubView>(), _deviceHub.LocalInfo())),
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

    /// <summary>A Claude Code permission prompt for the phones (M-48); answers null (ask on the PC) on any failure.</summary>
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
        base.Dispose();
    }
}
