using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
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
        X509Certificate2? lanCertificate = null)
    {
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
            return new BridgeStatus(_listening, LegacyHostsLocked(), _options.Port, _hub.Watchers, EndpointsLocked(),
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
    public PairingOffer? CreateOffer()
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
        var (code, expires) = _pairing.Begin();
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
            await ServeAsync(network, ct);
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
        await ServeAsync(ssl, ct);
    }

    private async Task ServeAsync(Stream stream, CancellationToken ct)
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
        if (r.Head.Method == "POST")
        {
            body = await MiniHttp.ReadBodyAsync(stream, r.Head, r.Leftover, ct);
            if (body is null)
            {
                await MiniHttp.WriteResponseAsync(stream, 400, Error("bad_request", "Missing or oversized body."), ct);
                return;
            }
        }

        var (status, json) = await HandleRestAsync(r.Head, body, ct);
        await MiniHttp.WriteResponseAsync(stream, status, json, ct);
    }

    /// <summary>REST routing, separated from sockets so tests can drive it.</summary>
    public async Task<(int Status, string Json)> HandleRestAsync(HttpRequestHead head, byte[]? body, CancellationToken ct)
    {
        switch (head.Method, head.Path)
        {
            case ("GET", "/api/v1/info"):
                return (200, Json(new PcInfo(MobileProtocol.Schema, MobileProtocol.Protocol, _devices.PcId,
                    Environment.MachineName, AgentVersion, Environment.OSVersion.VersionString)));

            case ("POST", "/api/v1/pair"):
            {
                PairRequest? req;
                try { req = JsonSerializer.Deserialize<PairRequest>(body!, MobileProtocol.Json); }
                catch (JsonException) { req = null; }
                if (req is null) return (400, Error("bad_request", "Invalid pairing request."));

                var result = _pairing.TryPair(req.Code, req.DeviceName, req.DeviceModel);
                switch (result.Outcome)
                {
                    case PairOutcome.Paired:
                        _logger.LogInformation("Mobile device paired: {Name} ({DeviceId}).", req.DeviceName, result.DeviceId);
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

            case (_, "/api/v1/info" or "/api/v1/pair" or "/api/v1/endpoints" or "/api/v1/snapshot" or "/api/v1/live"):
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
