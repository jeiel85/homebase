using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// The live views this PC's Tray opens on its device page (<c>live.get</c>, DECISIONS M-44). Each call
/// renews a lease of <see cref="Lease"/>; a view nobody asked about for that long is closed, so leaving
/// the page (or hiding the window) stops the sampling it caused (M-5).
/// <list type="bullet">
/// <item><c>self</c>: this PC, from <see cref="LiveSnapshotHub"/> as a local watcher.</item>
/// <item><c>hub:&lt;pcId&gt;</c>: a PC this one linked to — its <c>/api/v1/live</c> WebSocket, with the token
/// this PC holds, over the link's paths (LAN pinned TLS, Tailscale, Cloudflare).</item>
/// <item><c>device:&lt;id&gt;</c>: a PC that linked to this one — this side has no token for it, so the
/// <see cref="DeviceHub"/> asks it to share its snapshots through its inbox poll.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayLiveService : IDisposable
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(10);

    /// <summary>A shared snapshot older than this no longer counts as live.</summary>
    public static readonly TimeSpan SharedFresh = TimeSpan.FromSeconds(8);

    /// <summary>How long a linked PC may take to start sharing before the view says it does not answer.</summary>
    public static readonly TimeSpan ShareStartTimeout = TimeSpan.FromSeconds(40);

    /// <summary>The first Agent version that shares its live view with a hub on request.</summary>
    public static readonly Version ShareVersion = new(0, 33, 0);

    private readonly LiveSnapshotHub _hub;
    private readonly IMobileDeviceStore _devices;
    private readonly DeviceHub? _deviceHub;
    private readonly HubLinkService? _links;
    private readonly TimeProvider _time;
    private readonly ILogger<TrayLiveService> _logger;
    private readonly bool _allowLoopback;
    private readonly object _gate = new();
    private readonly Dictionary<string, HubLiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Since, DateTimeOffset Touched)> _shares = new(StringComparer.Ordinal);
    private readonly ITimer _sweep;
    private SelfView? _self;

    public TrayLiveService(
        LiveSnapshotHub hub,
        IMobileDeviceStore devices,
        TimeProvider time,
        ILogger<TrayLiveService> logger,
        DeviceHub? deviceHub = null,
        HubLinkService? links = null,
        bool allowLoopback = false)
    {
        _hub = hub;
        _devices = devices;
        _deviceHub = deviceHub;
        _links = links;
        _time = time;
        _logger = logger;
        _allowLoopback = allowLoopback;
        _sweep = time.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public LiveView Get(string? target)
    {
        if (string.IsNullOrEmpty(target)) return new LiveView(LiveViewState.Unknown);
        if (target == LiveTarget.Self) return Self();
        if (LiveTarget.HubId(target) is { } pcId) return HubView(pcId);
        if (LiveTarget.DeviceId(target) is { } deviceId) return DeviceView(deviceId);
        return new LiveView(LiveViewState.Unknown);
    }

    private LiveView Self()
    {
        SelfView view;
        lock (_gate)
        {
            _self ??= new SelfView(_hub.Subscribe(local: true));
            _self.Touched = _time.GetUtcNow();
            view = _self;
        }
        return view.Latest is { } s
            ? new LiveView(LiveViewState.Live, s, "self", At: s.Ts)
            : new LiveView(LiveViewState.Connecting);
    }

    private LiveView HubView(string pcId)
    {
        HubLiveSession session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(pcId, out session!))
            {
                if (_links?.Hub(pcId) is not { } hub) return new LiveView(LiveViewState.Unknown);
                session = new HubLiveSession(hub, _allowLoopback, _time, _logger);
                _sessions[pcId] = session;
            }
            session.Touched = _time.GetUtcNow();
        }
        return session.View(_time.GetUtcNow());
    }

    private LiveView DeviceView(string deviceId)
    {
        if (_deviceHub is null) return new LiveView(LiveViewState.Unknown);
        var device = _devices.List().FirstOrDefault(d => d.DeviceId == deviceId);
        if (device is null || device.Kind != DeviceKind.Pc) return new LiveView(LiveViewState.Unknown);
        var entry = _deviceHub.Devices().Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (entry?.Status?.AppVersion is { } version && ParseVersion(version) is { } v && v < ShareVersion)
            return new LiveView(LiveViewState.Unsupported, Detail: version);

        var now = _time.GetUtcNow();
        DateTimeOffset since;
        lock (_gate)
        {
            // A lapsed request starts over: the other PC gets a fresh window to begin sharing.
            since = _shares.TryGetValue(deviceId, out var s) && now - s.Touched <= Lease ? s.Since : now;
            _shares[deviceId] = (since, now);
        }
        _deviceHub.WantLive(deviceId);
        var frame = _deviceHub.LiveFrame(deviceId);
        if (frame is { } f && now - f.At <= SharedFresh) return new LiveView(LiveViewState.Live, f.Snapshot, "shared", At: f.At);
        if (entry is { Online: false } || now - since > ShareStartTimeout)
            return new LiveView(LiveViewState.Unreachable, frame?.Snapshot, "shared", entry is { Online: false } ? "offline" : "no_answer", frame?.At);
        return new LiveView(LiveViewState.Connecting, frame?.Snapshot, "shared", At: frame?.At);
    }

    internal static Version? ParseVersion(string text)
    {
        var core = text.Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? v : null;
    }

    /// <summary>Closes the views nobody asked about within <see cref="Lease"/>.</summary>
    internal void Sweep()
    {
        var now = _time.GetUtcNow();
        var closing = new List<IDisposable>();
        lock (_gate)
        {
            if (_self is { } self && now - self.Touched > Lease)
            {
                closing.Add(self);
                _self = null;
            }
            foreach (var (pcId, session) in _sessions.Where(kv => now - kv.Value.Touched > Lease).ToList())
            {
                closing.Add(session);
                _sessions.Remove(pcId);
            }
            foreach (var id in _shares.Where(kv => now - kv.Value.Touched > Lease).Select(kv => kv.Key).ToList())
                _shares.Remove(id);
        }
        foreach (var c in closing)
        {
            try { c.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Closing a Tray live view failed"); }
        }
    }

    public void Dispose()
    {
        _sweep.Dispose();
        lock (_gate)
        {
            _self?.Dispose();
            _self = null;
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }

    /// <summary>This PC's own snapshots, as a local watcher of the hub.</summary>
    private sealed class SelfView : IDisposable
    {
        private readonly LiveSnapshotHub.Subscription _sub;
        private readonly CancellationTokenSource _cts = new();

        public SelfView(LiveSnapshotHub.Subscription sub)
        {
            _sub = sub;
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var s in _sub.Reader.ReadAllAsync(_cts.Token)) Latest = s;
                }
                catch (OperationCanceledException) { }
            });
        }

        public volatile LiveSnapshot? Latest;
        public DateTimeOffset Touched;

        public void Dispose()
        {
            _cts.Cancel();
            _sub.Dispose();
        }
    }
}

/// <summary>
/// One live view of a hub PC (M-44): the same WebSocket the phone app opens, authenticated with the token
/// this PC got when it linked (M-29), over the link's paths in order — LAN with the pinned certificate,
/// Tailscale in plain HTTP, Cloudflare with public TLS. Reconnects every few seconds while it is wanted.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HubLiveSession : IDisposable
{
    private const int MaxMessageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RejectedRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(6);

    private readonly LinkedHub _hub;
    private readonly bool _allowLoopback;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private volatile LiveSnapshot? _latest;
    private DateTimeOffset _latestAt;
    private volatile string? _via;
    private volatile string? _error;

    public DateTimeOffset Touched;

    public HubLiveSession(LinkedHub hub, bool allowLoopback, TimeProvider time, ILogger logger)
    {
        _hub = hub;
        _allowLoopback = allowLoopback;
        _time = time;
        _logger = logger;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public LiveView View(DateTimeOffset now)
    {
        var latest = _latest;
        if (latest is not null && now - _latestAt <= Stale) return new LiveView(LiveViewState.Live, latest, _via, At: _latestAt);
        if (_error is { } error) return new LiveView(LiveViewState.Unreachable, latest, _via, error, latest is null ? null : _latestAt);
        return new LiveView(LiveViewState.Connecting, latest, _via, At: latest is null ? null : _latestAt);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var rejected = false;
            string? lastError = null;
            foreach (var ep in _hub.Endpoints.Where(e => _allowLoopback || e.Transport != MobileTransport.Loopback))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var ws = await ConnectAsync(ep, ct);
                    _via = ep.Transport;
                    _error = null;
                    await ReceiveAsync(ws, ct);
                    lastError = "closed";
                    if (ws.CloseStatus == WebSocketCloseStatus.PolicyViolation) { rejected = true; lastError = "rejected"; }
                    break; // it was reachable: start over from the first path after a pause
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (UnauthorizedAccessException)
                {
                    rejected = true;
                    lastError = "rejected";
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException or JsonException)
                {
                    lastError = $"{ep.Transport}: {ex.GetBaseException().Message}";
                }
            }
            _error = lastError ?? "no usable path";
            _logger.LogDebug("Live view of {Name}: {Error}", _hub.PcName, _error);
            try { await Task.Delay(rejected ? RejectedRetry : RetryDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(MobileEndpoint ep, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.Proxy = null;
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _hub.Token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ws.Options.CollectHttpResponseDetails = true;
            if (ep.Tls && ep.CertSha256 is { Length: > 0 } pin)
            {
                // Self-signed LAN certificate (M-15): accept exactly the pinned one, nothing else.
                ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && string.Equals(HubClient.MobileTlsPin(cert), pin, StringComparison.OrdinalIgnoreCase);
            }
            var http = HubClient.Url(ep, "/api/v1/live");
            var uri = new UriBuilder(http) { Scheme = ep.Tls ? "wss" : "ws" }.Uri;
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(ConnectTimeout);
            await ws.ConnectAsync(uri, connect.Token);
            return ws;
        }
        catch (WebSocketException) when (ws.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // The hub no longer knows this PC (unlinked there): say so instead of retrying every few seconds.
            ws.Dispose();
            throw new UnauthorizedAccessException("rejected");
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new IOException("Live snapshot too large.");
            if (!result.EndOfMessage) continue;
            var snapshot = JsonSerializer.Deserialize<LiveSnapshot>(message.GetBuffer().AsSpan(0, (int)message.Length), MobileProtocol.Json);
            message.SetLength(0);
            if (snapshot is null || snapshot.Schema != MobileProtocol.Schema) continue;
            _latest = snapshot;
            _latestAt = _time.GetUtcNow();
        }
    }

    // The token stays valid for the loop that is winding down, so the source is cancelled, not disposed.
    public void Dispose() => _cts.Cancel();
}
