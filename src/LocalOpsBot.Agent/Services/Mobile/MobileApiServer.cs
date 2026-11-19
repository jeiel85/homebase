using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// The phone-facing API (<c>docs/MOBILE_PROTOCOL.md</c>). Listens on explicit addresses only, never
/// a wildcard, and re-binds when the address set changes:
/// <list type="bullet">
/// <item>Tailscale address(es), plain HTTP (<c>mobile:tailscale</c>, default on; M-1).</item>
/// <item>Private LAN address(es) of gateway adapters, TLS with a pinned self-signed certificate
/// (<c>mobile:lan</c>; M-15).</item>
/// <item>127.0.0.1, plain HTTP, for the Cloudflare Tunnel connector (<c>mobile:cloudflare</c>; M-17)
/// or development (<c>mobile:bindLoopback</c>).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MobileApiServer : BackgroundService
{
    private static readonly TimeSpan RebindInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// How long a request body may take to arrive (a 4 MiB chunk over a 0.5 Mbps uplink takes about 70 s). Without it a
    /// slow sender could hold one of the connections indefinitely.
    /// </summary>
    internal static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(120);
    /// <summary>Body cap for a transfer's description and hashes (M-64): 20 names of up to 1024 characters, sealed.</summary>
    internal const int TransferInfoBodyBytes = 256 * 1024;
    private const int MaxConnections = 32;

    private enum PathKind { Tailnet, Lan, Loopback }

    private sealed record Binding(IPAddress Address, PathKind Kind, int PrefixLength)
    {
        public string Key => $"{Kind}:{Address}/{PrefixLength}";
    }

    private readonly LiveSnapshotHub _hub;
    private readonly PairingService _pairing;
    private readonly IMobileDeviceStore _devices;
    private readonly TailscaleAddressResolver _resolver;
    private readonly LanAddressResolver _lanResolver;
    private readonly CloudflareTunnel? _tunnel;
    private readonly DeviceHub? _deviceHub;
    private DeviceGroupService? _group;
    private readonly ApprovalBroker? _approvals;
    private readonly OnDemandAdvice? _advice;
    private readonly ClipShareService? _clips;
    private readonly FileShareService? _files;
    private readonly MobileOptions _options;
    private readonly ILogger<MobileApiServer> _logger;
    private readonly SemaphoreSlim _connections = new(MaxConnections);
    private readonly object _gate = new();
    private IReadOnlyList<Binding> _bound = Array.Empty<Binding>();
    private string? _magicDns;
    private X509Certificate2? _lanCertificate;
    private string? _lanPin;
    private bool _listening;

    public MobileApiServer(
        LiveSnapshotHub hub,
        PairingService pairing,
        IMobileDeviceStore devices,
        TailscaleAddressResolver resolver,
        LanAddressResolver lanResolver,
        IOptions<MobileOptions> options,
        ILogger<MobileApiServer> logger,
        CloudflareTunnel? tunnel = null,
        X509Certificate2? lanCertificate = null,
        DeviceHub? deviceHub = null,
        ApprovalBroker? approvals = null,
        OnDemandAdvice? advice = null,
        ClipShareService? clips = null,
        FileShareService? files = null)
    {
        _clips = clips;
        _files = files;
        _deviceHub = deviceHub;
        _approvals = approvals;
        _advice = advice;
        _hub = hub;
        _pairing = pairing;
        _devices = devices;
        _resolver = resolver;
        _lanResolver = lanResolver;
        _tunnel = tunnel;
        _options = options.Value;
        _logger = logger;
        if (lanCertificate is not null) UseLanCertificate(lanCertificate);
    }

    public static string AgentVersion =>
        typeof(MobileApiServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.0.0";

    private bool WantsLoopback => _options.BindLoopback || _options.Cloudflare.Enabled;

    public BridgeStatus Status()
    {
        lock (_gate)
        {
            return new BridgeStatus(_listening, LegacyHostsLocked(), _options.Port, _hub.RemoteWatchers, EndpointsLocked(),
                new MobileOptionsSummary(_options.Tailscale, _options.Lan, _options.Cloudflare.Enabled),
                _tunnel?.Status());
        }
    }

    /// <summary>Every path a phone can use right now, in the app's default preference order.</summary>
    public IReadOnlyList<MobileEndpoint> Endpoints()
    {
        lock (_gate) return EndpointsLocked();
    }

    /// <summary>A fresh one-time pairing offer for the QR code, or <c>null</c> while no path is up.</summary>
    /// <summary>
    /// A pairing offer with a fresh one-time code. <paramref name="introduction"/>: a group introduction
    /// (M-47), whose code does not replace the QR code on screen.
    /// </summary>
    public PairingOffer? CreateOffer(bool introduction = false)
    {
        IReadOnlyList<string> hosts;
        IReadOnlyList<MobileEndpoint> endpoints;
        lock (_gate)
        {
            if (!_listening) return null;
            hosts = LegacyHostsLocked();
            endpoints = EndpointsLocked();
        }
        if (endpoints.Count == 0) return null;
        var (code, expires) = introduction ? _pairing.BeginIntroduction() : _pairing.Begin();
        return new PairingOffer(1, _devices.PcId, Environment.MachineName, hosts, _options.Port, code, expires, endpoints);
    }

    private IReadOnlyList<MobileEndpoint> EndpointsLocked()
    {
        var list = new List<MobileEndpoint>();
        if (!_listening) return list;
        foreach (var b in _bound.Where(b => b.Kind == PathKind.Lan))
            list.Add(new MobileEndpoint(MobileTransport.Lan, b.Address.ToString(), _options.Port, true, _lanPin));
        if (_bound.Any(b => b.Kind == PathKind.Tailnet) && _magicDns is { } dns)
            list.Add(new MobileEndpoint(MobileTransport.Tailscale, dns, _options.Port, false));
        foreach (var b in _bound.Where(b => b.Kind == PathKind.Tailnet))
            list.Add(new MobileEndpoint(MobileTransport.Tailscale, b.Address.ToString(), _options.Port, false));
        if (_bound.Any(b => b.Kind == PathKind.Loopback) && _tunnel?.Endpoint() is { } cf)
            list.Add(cf);
        if (_options.BindLoopback)
            list.Add(new MobileEndpoint(MobileTransport.Loopback, IPAddress.Loopback.ToString(), _options.Port, false));
        return list;
    }

    /// <summary>v1 <c>hosts</c>: what pre-endpoint apps can use (plain HTTP — Tailscale, and loopback in development).</summary>
    private IReadOnlyList<string> LegacyHostsLocked() =>
        EndpointsLocked().Where(e => e.Transport is MobileTransport.Tailscale or MobileTransport.Loopback)
            .Select(e => e.Host).ToList();

    private void UseLanCertificate(X509Certificate2 cert)
    {
        _lanCertificate = cert;
        _lanPin = MobileTlsCertificate.Sha256Hex(cert);
    }

    private IReadOnlyList<Binding> DesiredBindings()
    {
        var list = new List<Binding>();
        if (_options.Tailscale)
            list.AddRange(_resolver.FindTailnetAddresses().Select(a => new Binding(a, PathKind.Tailnet, 32)));
        if (_options.Lan && EnsureLanCertificate())
            list.AddRange(_lanResolver.FindLanAddresses().Select(a => new Binding(a.Address, PathKind.Lan, a.PrefixLength)));
        if (WantsLoopback) list.Add(new Binding(IPAddress.Loopback, PathKind.Loopback, 8));
        return list;
    }

    private bool EnsureLanCertificate()
    {
        if (_lanCertificate is not null) return true;
        try
        {
            UseLanCertificate(MobileTlsCertificate.LoadOrCreate(_logger));
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Mobile API: the LAN TLS certificate is unavailable; the LAN path stays off.");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var warnedNoAddress = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var bind = DesiredBindings();
            var phoneFacing = bind.Count(b => b.Kind != PathKind.Loopback);
            if (phoneFacing == 0 && !_options.Cloudflare.Enabled && !warnedNoAddress)
            {
                _logger.LogWarning(
                    "Mobile API: no usable address (Tailscale {Tailscale}, LAN {Lan}) — phones can't connect yet. Retrying every {Seconds}s.",
                    _options.Tailscale ? "on" : "off", _options.Lan ? "on" : "off", RebindInterval.TotalSeconds);
                warnedNoAddress = true;
            }
            if (phoneFacing > 0) warnedNoAddress = false;

            if (bind.Count == 0)
            {
                await DelaySafe(RebindInterval, stoppingToken);
                continue;
            }

            var magicDns = bind.Any(b => b.Kind == PathKind.Tailnet) ? _resolver.FindMagicDnsName() : null;
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var listeners = new List<(TcpListener Listener, Binding Binding)>();
            try
            {
                foreach (var b in bind)
                {
                    var l = new TcpListener(b.Address, _options.Port);
                    l.Start();
                    listeners.Add((l, b));
                }
                lock (_gate) { _bound = bind; _magicDns = magicDns; _listening = true; }
                _logger.LogInformation("Mobile API listening on {Endpoints}.",
                    string.Join(", ", bind.Select(b => $"{b.Address}:{_options.Port} ({b.Kind}{(b.Kind == PathKind.Lan ? ", TLS" : "")})")));

                var accepts = listeners.Select(l => AcceptLoopAsync(l.Listener, l.Binding.Kind, cycle.Token)).ToList();
                var keys = bind.Select(b => b.Key).ToHashSet();
                // Re-check the address set periodically; rebind when it changes.
                while (!stoppingToken.IsCancellationRequested)
                {
                    await DelaySafe(RebindInterval, stoppingToken);
                    if (!DesiredBindings().Select(b => b.Key).ToHashSet().SetEquals(keys))
                    {
                        _logger.LogInformation("Mobile API addresses changed; re-binding.");
                        break;
                    }
                }
                cycle.Cancel();
                foreach (var l in listeners) l.Listener.Stop();
                await Task.WhenAll(accepts);
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Mobile API could not bind port {Port}; retrying in {Seconds}s.",
                    _options.Port, RebindInterval.TotalSeconds);
                foreach (var l in listeners) l.Listener.Stop();
                await DelaySafe(RebindInterval, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A fault here must not stop the Agent host (and with it Telegram monitoring).
                _logger.LogError(ex, "Mobile API failed; restarting it in {Seconds}s.", RebindInterval.TotalSeconds);
                foreach (var l in listeners) l.Listener.Stop();
                await DelaySafe(RebindInterval, stoppingToken);
            }
            finally
            {
                lock (_gate) { _listening = false; }
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, PathKind kind, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }

            if (!await _connections.WaitAsync(0, ct))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(client, kind, ct); }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or WebSocketException or AuthenticationException)
                {
                    _logger.LogDebug("Mobile connection ended: {Message}", ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Mobile connection failed.");
                }
                finally
                {
                    client.Dispose();
                    _connections.Release();
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Defense in depth behind the bind address and firewall rule: each listener accepts only the
    /// peers its path can produce.
    /// </summary>
    private bool PeerAllowed(IPAddress peer, PathKind kind)
    {
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        switch (kind)
        {
            case PathKind.Tailnet:
                return TailscaleAddressResolver.IsTailnetV4(peer) || IPAddress.IsLoopback(peer);
            case PathKind.Lan:
                IReadOnlyList<Binding> bound;
                lock (_gate) bound = _bound;
                return LanAddressResolver.IsPrivateV4(peer) && LanAddressResolver.InSameSubnet(peer,
                    bound.Where(b => b.Kind == PathKind.Lan).Select(b => new LanAddress(b.Address, b.PrefixLength)));
            default:
                return IPAddress.IsLoopback(peer);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, PathKind kind, CancellationToken ct)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !PeerAllowed(remote.Address, kind))
            return;

        var network = client.GetStream();
        if (kind != PathKind.Lan)
        {
            await ServeAsync(network, kind, ct);
            return;
        }

        // LAN has no WireGuard underneath: TLS with the pinned self-signed certificate (M-15).
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(HeadTimeout);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _lanCertificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
            }, handshake.Token);
        }
        await ServeAsync(ssl, kind, ct);
    }

    private async Task ServeAsync(Stream stream, PathKind kind, CancellationToken ct)
    {
        (HttpRequestHead Head, byte[] Leftover)? request;
        using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headCts.CancelAfter(HeadTimeout);
            request = await MiniHttp.ReadHeadAsync(stream, headCts.Token);
        }
        if (request is not { } r)
        {
            await MiniHttp.WriteResponseAsync(stream, 400, Error("bad_request", "Malformed request."), ct);
            return;
        }

        if (r.Head.Path == "/api/v1/live" && r.Head.Method == "GET")
        {
            await HandleLiveAsync(stream, r.Head, ct);
            return;
        }

        byte[]? body = null;
        var chunkTurn = false;
        try
        {
            if (r.Head.Method == "POST")
            {
                var maxBody = MiniHttp.MaxBodyBytes;
                if (r.Head.Path == LiveSharePath)
                {
                    // The one large body: only a paired device may make us read it (M-46).
                    if (_devices.Authenticate(r.Head.BearerToken() ?? "") is null)
                    {
                        await MiniHttp.WriteResponseAsync(stream, 401, Error("unauthorized", "This device is not paired with this PC."), ct);
                        return;
                    }
                    maxBody = MiniHttp.MaxSnapshotBodyBytes;
                }
                else if (r.Head.Path == ClipProtocol.Path)
                {
                    // Clipboard text (M-62) can be long; read it only for a paired device.
                    if (_devices.Authenticate(r.Head.BearerToken() ?? "") is null)
                    {
                        await MiniHttp.WriteResponseAsync(stream, 401, Error("unauthorized", "This device is not paired with this PC."), ct);
                        return;
                    }
                    maxBody = ClipProtocol.MaxBodyBytes;
                }
                else if (r.Head.Path is TransferProtocol.Path or TransferProtocol.ChunkPath or TransferProtocol.DonePath)
                {
                    // Files from a device (M-64): read only for a paired device, a chunk only on its turn.
                    if (_files is null)
                    {
                        await MiniHttp.WriteResponseAsync(stream, 404, Error("not_found", "Not found."), ct);
                        return;
                    }
                    if (_devices.Authenticate(r.Head.BearerToken() ?? "") is null)
                    {
                        await MiniHttp.WriteResponseAsync(stream, 401, Error("unauthorized", "This device is not paired with this PC."), ct);
                        return;
                    }
                    if (r.Head.Path == TransferProtocol.ChunkPath)
                    {
                        if (!await _files.EnterChunkAsync(ct))
                        {
                            await MiniHttp.WriteResponseAsync(stream, 503, Error(TransferErrors.Busy, "Busy with other chunks; send this one again shortly."), ct);
                            return;
                        }
                        chunkTurn = true;
                        maxBody = TransferProtocol.MaxChunkBodyBytes;
                    }
                    else
                    {
                        maxBody = TransferInfoBodyBytes;
                    }
                }
                using (var bodyLimit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    bodyLimit.CancelAfter(BodyTimeout);
                    try { body = await MiniHttp.ReadBodyAsync(stream, r.Head, r.Leftover, bodyLimit.Token, maxBody); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { body = null; }
                }
                if (body is null)
                {
                    await MiniHttp.WriteResponseAsync(stream, 400, Error("bad_request", "Missing, oversized or too slow body."), ct);
                    return;
                }
            }

            // Loopback is where the Cloudflare connector delivers internet traffic (M-17).
            var viaTunnel = kind == PathKind.Loopback && (_options.Cloudflare.Enabled || CameThroughCloudflare(r.Head));
            var (status, json) = await HandleRestAsync(r.Head, body, ct, viaTunnel);
            await MiniHttp.WriteResponseAsync(stream, status, json, ct);
        }
        finally
        {
            if (chunkTurn) _files!.LeaveChunk();
        }
    }

    /// <summary>
    /// Whether a request carries the headers Cloudflare's edge adds (and <c>cloudflared</c> passes on).
    /// Only trusted to make an answer smaller, never larger: a local process could send them too.
    /// </summary>
    public static bool CameThroughCloudflare(HttpRequestHead head) =>
        head.Header("Cf-Ray") is { Length: > 0 }
        || head.Header("Cf-Connecting-Ip") is { Length: > 0 }
        || head.Header("Cdn-Loop")?.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The unauthenticated identity probe. Over the Cloudflare Tunnel the API is reachable from the
    /// whole internet, so an anonymous caller there no longer learns the PC name or OS version (#15);
    /// a paired device's bearer token still gets the full answer. Every field stays present because
    /// the app's parser requires <c>pcId</c> and <c>pcName</c> (it shows only <c>agentVersion</c>).
    /// </summary>
    public PcInfo Info(HttpRequestHead head, bool viaTunnel)
    {
        var full = !viaTunnel;
        if (!full && head.BearerToken() is { Length: > 0 } token)
            full = _devices.Authenticate(token) is not null;
        return new PcInfo(MobileProtocol.Schema, MobileProtocol.Protocol, _devices.PcId,
            full ? Environment.MachineName : "", AgentVersion, full ? Environment.OSVersion.VersionString : "");
    }

    /// <summary>
    /// REST routing, separated from sockets so tests can drive it. <paramref name="viaTunnel"/>: the
    /// request reached the loopback listener through the Cloudflare Tunnel, i.e. from the internet.
    /// </summary>
    public async Task<(int Status, string Json)> HandleRestAsync(HttpRequestHead head, byte[]? body, CancellationToken ct, bool viaTunnel = false)
    {
        switch (head.Method, head.Path)
        {
            case ("GET", "/api/v1/info"):
                return (200, Json(Info(head, viaTunnel)));

            case ("POST", "/api/v1/pair"):
            {
                PairRequest? req;
                try { req = JsonSerializer.Deserialize<PairRequest>(body!, MobileProtocol.Json); }
                catch (JsonException) { req = null; }
                if (req is null) return (400, Error("bad_request", "Invalid pairing request."));

                var result = _pairing.TryPair(req.Code, req.DeviceName, req.DeviceModel, req.Kind);
                switch (result.Outcome)
                {
                    case PairOutcome.Paired:
                        _logger.LogInformation("Device paired: {Name} ({DeviceId}, {Kind}).", req.DeviceName, result.DeviceId,
                            DeviceKind.Normalize(req.Kind));
                        // Paired again after being taken out of the group: it is back in (M-47).
                        _group?.Readmit(DeviceKind.Normalize(req.Kind) == DeviceKind.Pc && req.PcId is { Length: > 0 } pcId
                            ? GroupKeys.Pc(pcId)
                            : GroupKeys.PhoneByName(req.DeviceName ?? "", req.DeviceModel));
                        return (200, Json(new PairResponse(MobileProtocol.Schema, result.DeviceId!, result.Token!,
                            _devices.PcId, Environment.MachineName)));
                    case PairOutcome.Expired:
                        return (410, Error("code_expired", "The pairing code expired. Show a new QR code."));
                    case PairOutcome.Locked:
                        _logger.LogWarning("Mobile pairing locked after repeated wrong codes.");
                        return (429, Error("locked", "Too many wrong codes. Try again in a few minutes."));
                    default:
                        return (400, Error("invalid_code", "The pairing code is not valid."));
                }
            }

            case ("GET", "/api/v1/endpoints"):
                if (_devices.Authenticate(head.BearerToken() ?? "") is null)
                    return (401, Error("unauthorized", "This phone is not paired with this PC."));
                return (200, Json(new EndpointsResponse(MobileProtocol.Schema, Endpoints())));

            case ("GET", "/api/v1/snapshot"):
            {
                if (_devices.Authenticate(head.BearerToken() ?? "") is null)
                    return (401, Error("unauthorized", "This phone is not paired with this PC."));
                using var sub = _hub.Subscribe();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                try { return (200, Json(await sub.Reader.ReadAsync(wait.Token))); }
                catch (OperationCanceledException) { return (503, Error("timeout", "No snapshot was produced in time.")); }
            }

            case ("POST", "/api/v1/push"):
            {
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This phone is not paired with this PC."));
                // Pushes carry every alert and the Claude approval prompts (with their ids). A linked PC
                // never takes pushes, so its token must not subscribe an endpoint of its own choosing.
                if (device.Kind == DeviceKind.Pc) return (403, Error("forbidden", "Only a phone registers for push."));
                PushSubscriptionRequest? req;
                try { req = JsonSerializer.Deserialize<PushSubscriptionRequest>(body!, MobileProtocol.Json); }
                catch (JsonException) { req = null; }
                if (PushPayload.Validate(req) is { } invalid)
                    return (400, Error("invalid_subscription", invalid));

                var existing = _devices.PushTargets().FirstOrDefault(t => t.DeviceId == device.DeviceId)?.Subscription;
                var muted = PushCategories.Normalize(req!.MutedCategories);
                // Same normal form as the muted list: sorted, distinct, null when empty (M-62).
                var features = PushCategories.Normalize(req.Features);
                var unchanged = existing is not null && existing.Endpoint == req.Endpoint
                    && existing.P256dh == req.P256dh && existing.Auth == req.Auth
                    && PushCategories.SameMuted(existing.MutedCategories, muted)
                    && PushCategories.SameMuted(existing.Features, features);
                if (!unchanged)
                {
                    // The app re-registers on every reconnect; only a real change rewrites state.json.
                    _devices.SetPushSubscription(device.DeviceId,
                        new PushSubscription(req.Endpoint, req.P256dh, req.Auth, DateTimeOffset.UtcNow, muted, features));
                    _logger.LogInformation("Push subscription {Action} for {Name} ({DeviceId}) at {Host}; muted: {Muted}; features: {Features}.",
                        existing is null ? "registered" : "updated", device.Name, device.DeviceId, new Uri(req.Endpoint).Host,
                        muted is null ? "none" : string.Join(",", muted), features is null ? "none" : string.Join(",", features));
                }
                return (200, Json(new PushSubscriptionResponse(MobileProtocol.Schema, device.DeviceId, true)));
            }

            case ("DELETE", "/api/v1/push"):
            {
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This phone is not paired with this PC."));
                _devices.SetPushSubscription(device.DeviceId, null);
                _logger.LogInformation("Push subscription removed for {Name} ({DeviceId}).", device.Name, device.DeviceId);
                return (200, Json(new PushSubscriptionResponse(MobileProtocol.Schema, device.DeviceId, false)));
            }

            case ("POST", "/api/v1/presence"):
            {
                if (_deviceHub is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                PresenceReport? report;
                try { report = JsonSerializer.Deserialize<PresenceReport>(body!, MobileProtocol.Json); }
                catch (JsonException) { report = null; }
                if (PresenceReport.Validate(report) is { } invalid) return (400, Error("invalid_presence", invalid));
                var answer = _deviceHub.Presence(device, report!);
                // A linked PC's paths reach this PC's own link to it when that one went stale (M-59).
                _group?.AdoptPaths(device, report!.Sanitized());
                return (200, Json(answer));
            }

            case ("GET", "/api/v1/devices"):
            {
                if (_deviceHub is null) return (404, Error("not_found", "Not found."));
                if (_devices.Authenticate(head.BearerToken() ?? "") is null)
                    return (401, Error("unauthorized", "This device is not paired with this PC."));
                return (200, Json(_deviceHub.Devices()));
            }

            case ("POST", "/api/v1/notifications"):
            {
                if (_deviceHub is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                if (!_deviceHub.CanRelay)
                    return (503, Error("forwarding_off", "Notification forwarding is turned off on this PC."));
                DeviceNotification? n;
                try { n = JsonSerializer.Deserialize<DeviceNotification>(body!, MobileProtocol.Json); }
                catch (JsonException) { n = null; }
                if (DeviceNotification.Validate(n) is { } invalid) return (400, Error("invalid_notification", invalid));
                // Accept at once: pushing on to the other phones can take seconds (ntfy), and the sender
                // only needs to know the PC has it. RelayAsync logs its own failures.
                _ = Task.Run(() => _deviceHub.RelayAsync(device, n!), CancellationToken.None);
                return (202, "{\"schema\":1,\"accepted\":true}");
            }

            case ("GET", "/api/v1/inbox"):
            {
                if (_deviceHub is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                if (!long.TryParse(head.QueryValue("after"), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var after))
                    after = 0;
                try
                {
                    // Null: too many linked PCs are already waiting. Any non-2xx makes the poller back off
                    // and ask again later with the same cursor, so nothing is lost (the inbox keeps 30 min).
                    return await _deviceHub.InboxAsync(device, after, ct) is { } inbox
                        ? (200, Json(inbox))
                        : (503, Error("busy", "Too many linked PCs are waiting; try again shortly."));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (200, Json(_deviceHub.Inbox.Read(after, device.DeviceId))); }
            }

            case (_, GroupPaths.Group or GroupPaths.Offer or GroupPaths.Join or GroupPaths.Introduce or GroupPaths.Remove or GroupPaths.Alias):
                return await GroupAsync(head, body, ct);

            case ("POST", LiveSharePath):
            {
                if (_deviceHub is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                // Phones have nothing to share this way; only a linked PC is asked to (M-46).
                if (device.Kind != DeviceKind.Pc) return (403, Error("forbidden", "Only a linked PC shares its live view."));
                LiveSnapshot? snapshot;
                try { snapshot = JsonSerializer.Deserialize<LiveSnapshot>(body!, MobileProtocol.Json); }
                catch (JsonException) { snapshot = null; }
                if (snapshot is null || snapshot.Schema != MobileProtocol.Schema || snapshot.System is null)
                    return (400, Error("invalid_snapshot", "Invalid live snapshot."));
                // Lists left out of the JSON come back null; the Tray reads them as empty.
                snapshot = snapshot with
                {
                    Apps = snapshot.Apps ?? Array.Empty<AppEntry>(),
                    Processes = snapshot.Processes ?? Array.Empty<ProcessEntry>(),
                    ClaudeSessions = snapshot.ClaudeSessions ?? Array.Empty<ClaudeSession>(),
                };
                return (200, Json(new LiveShareResponse(MobileProtocol.Schema, _deviceHub.AcceptLive(device, snapshot))));
            }

            case ("POST", "/api/v1/approval"):
            {
                // A phone's Allow / Deny for a Claude Code permission prompt (M-48).
                if (_approvals is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                // Allow runs what Claude Code asked for on this PC: only a phone the user holds answers.
                if (device.Kind == DeviceKind.Pc) return (403, Error("forbidden", "Only a phone answers approvals."));
                ApprovalAnswer? answer;
                try { answer = JsonSerializer.Deserialize<ApprovalAnswer>(body!, MobileProtocol.Json); }
                catch (JsonException) { answer = null; }
                return _approvals.Answer(answer?.Id, answer?.Decision, device.Name) switch
                {
                    ApprovalAnswerOutcome.Accepted => (200, "{\"schema\":1,\"accepted\":true}"),
                    ApprovalAnswerOutcome.Unknown => (410, Error("approval_gone", "This request was already answered, withdrawn or timed out.")),
                    _ => (400, Error("bad_request", "Invalid approval answer.")),
                };
            }

            case ("POST", "/api/v1/advise"):
            {
                // The local LLM's health advice, asked for from the app (M-60). Any paired device may ask:
                // it reads this PC's state like the live view does and changes nothing.
                if (_advice is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                var reply = await _advice.GetAsync(ct);
                _logger.LogInformation("AI advice for {Name}: {Outcome}{Cached}.", device.Name,
                    reply.Ok ? "ok" : reply.Code, reply.Cached ? " (reused)" : "");
                return (200, Json(AdviceResponse.From(reply)));
            }

            case ("POST", ClipProtocol.KeyPath):
            {
                // A device agrees its clipboard key with this PC by ECDH (M-62): only public keys travel.
                if (_clips is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                return _clips.AgreeKey(device, Read<ClipKeyExchange>(body)) is { } answer
                    ? (200, Json(answer))
                    : (400, Error(ClipErrors.InvalidKey, "The public key is not a P-256 point."));
            }

            case ("POST", ClipProtocol.Path):
            {
                // Clipboard text from a phone or a linked PC (M-62): the Tray puts it on this PC's clipboard.
                if (_clips is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                return await _clips.AcceptAsync(device, Read<ClipEnvelope>(body), ct) switch
                {
                    ClipAcceptOutcome.Accepted => (200, "{\"schema\":1,\"accepted\":true}"),
                    ClipAcceptOutcome.KeyNeeded => (409, Error(ClipErrors.KeyNeeded, "Agree a clipboard key first (POST /api/v1/clip/key).")),
                    ClipAcceptOutcome.NoUserSession => (409, Error(ClipErrors.NoUserSession, "Nobody is signed in to this PC to receive it.")),
                    ClipAcceptOutcome.ClipboardBusy => (503, Error(ClipErrors.ClipboardBusy, "The clipboard was busy; nothing was pasted. Send it again.")),
                    _ => (400, Error(ClipErrors.Invalid, "Invalid clipboard text.")),
                };
            }

            case ("GET", ClipProtocol.Path):
            {
                // Text too long for a push that this PC offered to the asking phone (M-62).
                if (_clips is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                return _clips.Fetch(device, head.QueryValue("id")) is { } envelope
                    ? (200, Json(envelope))
                    : (404, Error("clip_gone", "This text is no longer here (it is kept for 10 minutes)."));
            }

            case (_, TransferProtocol.Path or TransferProtocol.ChunkPath or TransferProtocol.DonePath) when _files is not null:
            {
                // Files from a phone or a linked PC (M-64): checked here, saved by the signed-in user's Tray.
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                var id = head.QueryValue("id");
                var reply = (head.Method, head.Path) switch
                {
                    ("POST", TransferProtocol.Path) => _files.Create(device, Read<ClipEnvelope>(body)),
                    ("GET", TransferProtocol.Path) => _files.Get(device, id),
                    ("DELETE", TransferProtocol.Path) => _files.Cancel(device, id),
                    ("POST", TransferProtocol.ChunkPath) => await _files.ChunkAsync(device, id, head.QueryValue("f"), head.QueryValue("n"), body, ct),
                    ("POST", TransferProtocol.DonePath) => await _files.DoneAsync(device, id, Read<ClipEnvelope>(body), ct),
                    _ => new TransferReply(405, Error("method_not_allowed", "Method not allowed.")),
                };
                return (reply.Status, reply.Json);
            }

            case ("DELETE", "/api/v1/device"):
            {
                if (_deviceHub is null) return (404, Error("not_found", "Not found."));
                var device = _devices.Authenticate(head.BearerToken() ?? "");
                if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
                return (200, Json(new UnpairResponse(MobileProtocol.Schema, device.DeviceId, _deviceHub.Unpair(device))));
            }

            case (_, "/api/v1/info" or "/api/v1/pair" or "/api/v1/endpoints" or "/api/v1/snapshot" or "/api/v1/live" or "/api/v1/push"
                or "/api/v1/presence" or "/api/v1/devices" or "/api/v1/notifications" or "/api/v1/inbox" or "/api/v1/device"
                or LiveSharePath or "/api/v1/approval" or "/api/v1/advise" or ClipProtocol.Path or ClipProtocol.KeyPath):
                return (405, Error("method_not_allowed", "Method not allowed."));

            default:
                return (404, Error("not_found", "Not found."));
        }
    }

    private async Task HandleLiveAsync(Stream stream, HttpRequestHead head, CancellationToken ct)
    {
        var device = _devices.Authenticate(head.BearerToken() ?? "");
        if (device is null)
        {
            await MiniHttp.WriteResponseAsync(stream, 401, Error("unauthorized", "This phone is not paired with this PC."), ct);
            return;
        }
        if (!head.IsWebSocketUpgrade)
        {
            await MiniHttp.WriteResponseAsync(stream, 400, Error("bad_request", "WebSocket upgrade required."), ct);
            return;
        }

        await MiniHttp.WriteUpgradeAsync(stream, head.Header("Sec-WebSocket-Key")!, ct);
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        });
        using var sub = _hub.Subscribe();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Drain client frames so a close (or a dead peer) ends the session promptly.
        var receive = Task.Run(async () =>
        {
            var buf = new byte[256];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var res = await socket.ReceiveAsync(buf, linked.Token);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
            finally { linked.Cancel(); }
        }, CancellationToken.None);

        try
        {
            await foreach (var snapshot in sub.Reader.ReadAllAsync(linked.Token))
            {
                // Authorization is re-checked on every push, so a phone revoked in the Tray loses
                // the live view within one interval instead of keeping it until it disconnects.
                if (!_devices.IsPaired(device.DeviceId))
                {
                    _logger.LogInformation("Closing live view of revoked device {DeviceId}.", device.DeviceId);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, RevokedCloseReason, CancellationToken.None);
                    break;
                }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, MobileProtocol.Json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, linked.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            linked.Cancel();
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch (Exception ex) when (ex is WebSocketException or IOException) { }
            }
            await receive;
        }
    }

    /// <summary>The device group (M-47); set once both exist (they need each other).</summary>
    public void AttachGroup(DeviceGroupService group) => _group = group;

    private async Task<(int, string)> GroupAsync(HttpRequestHead head, byte[]? body, CancellationToken ct)
    {
        var device = _devices.Authenticate(head.BearerToken() ?? "");
        if (device is null) return (401, Error("unauthorized", "This device is not paired with this PC."));
        if (_group is null) return (404, Error("not_found", "Not found."));
        switch (head.Method, head.Path)
        {
            case ("GET", GroupPaths.Group):
                return (200, Json(_group.Describe()));
            case ("POST", GroupPaths.Offer):
                return CreateOffer(introduction: true) is { } own
                    ? (200, Json(own))
                    : (503, Error("not_listening", "This PC has no path to offer right now."));
            case ("POST", GroupPaths.Join):
            {
                var req = Read<GroupJoinRequest>(body);
                if (req?.Offer is not { Code.Length: > 0, Endpoints.Count: > 0 } offer) return (400, Error("bad_request", "Invalid offer."));
                return (200, Json(await _group.JoinAsync(offer, ct)));
            }
            case ("POST", GroupPaths.Introduce):
            {
                var req = Read<GroupIntroduceRequest>(body);
                if (req is not { PcId.Length: > 0 }) return (400, Error("bad_request", "Missing pcId."));
                return await _group.IntroduceAsync(req.PcId, ct) is { } introduced
                    ? (200, Json(introduced))
                    : (404, Error("not_linked", "This PC cannot introduce that PC."));
            }
            case ("POST", GroupPaths.Remove):
            {
                // From a member PC that removed a device; applied here without passing it on (one hop).
                if (device.Kind != DeviceKind.Pc) return (403, Error("forbidden", "Only a linked PC removes devices from the group."));
                var req = Read<GroupRemoveRequest>(body);
                if (!GroupKeys.IsValid(req?.Key)) return (400, Error("bad_request", "Invalid key."));
                return (200, Json(new { schema = MobileProtocol.Schema, removed = await _group.RemoveAsync(req!.Key, fanOut: false, ct) }));
            }
            case ("POST", GroupPaths.Alias):
            {
                // From any member: a phone where the user named a PC, or another PC's tray (M-51).
                var req = Read<GroupAliasRequest>(body);
                if (req is not { Aliases: not null } || req.Schema != MobileProtocol.Schema) return (400, Error("bad_request", "Invalid aliases."));
                return (200, Json(new GroupAliasResponse(MobileProtocol.Schema, _group.AcceptAliases(req.Aliases))));
            }
            default:
                return (405, Error("method_not_allowed", "Method not allowed."));
        }
    }

    private static T? Read<T>(byte[]? body) where T : class
    {
        if (body is null) return null;
        try { return JsonSerializer.Deserialize<T>(body, MobileProtocol.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Where a linked PC shares its live snapshots while this PC's Tray looks at it (M-46).</summary>
    public const string LiveSharePath = "/api/v1/live-snapshot";

    /// <summary>WebSocket close reason (status 1008) when the device was revoked; the app treats it like 401.</summary>
    public const string RevokedCloseReason = "revoked";

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, MobileProtocol.Json);

    private static string Error(string code, string message) =>
        Json(new ErrorResponse(MobileProtocol.Schema, code, message));

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
