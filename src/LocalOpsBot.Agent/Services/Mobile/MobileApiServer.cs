using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// The phone-facing API (<c>docs/MOBILE_PROTOCOL.md</c>). Listens only on this PC's Tailscale
/// address(es) — plus loopback when <c>mobile:bindLoopback</c> is set for development — and
/// re-binds when the tailnet address appears or changes. Never binds a LAN or wildcard address.
/// </summary>
public sealed class MobileApiServer : BackgroundService
{
    private static readonly TimeSpan RebindInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);
    private const int MaxConnections = 32;

    private readonly LiveSnapshotHub _hub;
    private readonly PairingService _pairing;
    private readonly IMobileDeviceStore _devices;
    private readonly TailscaleAddressResolver _resolver;
    private readonly MobileOptions _options;
    private readonly ILogger<MobileApiServer> _logger;
    private readonly SemaphoreSlim _connections = new(MaxConnections);
    private readonly object _gate = new();
    private IReadOnlyList<string> _hosts = Array.Empty<string>();
    private bool _listening;

    public MobileApiServer(
        LiveSnapshotHub hub,
        PairingService pairing,
        IMobileDeviceStore devices,
        TailscaleAddressResolver resolver,
        IOptions<MobileOptions> options,
        ILogger<MobileApiServer> logger)
    {
        _hub = hub;
        _pairing = pairing;
        _devices = devices;
        _resolver = resolver;
        _options = options.Value;
        _logger = logger;
    }

    public static string AgentVersion =>
        typeof(MobileApiServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.0.0";

    public BridgeStatus Status()
    {
        lock (_gate) return new BridgeStatus(_listening, _hosts, _options.Port, _hub.Watchers);
    }

    /// <summary>A fresh one-time pairing offer for the QR code, or <c>null</c> while not listening.</summary>
    public PairingOffer? CreateOffer()
    {
        IReadOnlyList<string> hosts;
        lock (_gate)
        {
            if (!_listening || _hosts.Count == 0) return null;
            hosts = _hosts;
        }
        var (code, expires) = _pairing.Begin();
        return new PairingOffer(1, _devices.PcId, Environment.MachineName, hosts, _options.Port, code, expires);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var warnedNoTailnet = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var addresses = _resolver.FindTailnetAddresses().ToList();
            if (addresses.Count == 0 && !warnedNoTailnet)
            {
                _logger.LogWarning(
                    "Mobile API: no Tailscale (100.64.0.0/10) address on this PC — phones can't connect until Tailscale is up. Retrying every {Seconds}s.",
                    RebindInterval.TotalSeconds);
                warnedNoTailnet = true;
            }
            if (addresses.Count > 0) warnedNoTailnet = false;

            var bind = addresses.ToList();
            if (_options.BindLoopback) bind.Add(IPAddress.Loopback);
            if (bind.Count == 0)
            {
                await DelaySafe(RebindInterval, stoppingToken);
                continue;
            }

            var hosts = new List<string>();
            if (addresses.Count > 0 && _resolver.FindMagicDnsName() is { } dns) hosts.Add(dns);
            hosts.AddRange(bind.Select(a => a.ToString()));

            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var listeners = new List<TcpListener>();
            try
            {
                foreach (var ip in bind)
                {
                    var l = new TcpListener(ip, _options.Port);
                    l.Start();
                    listeners.Add(l);
                }
                lock (_gate) { _hosts = hosts; _listening = true; }
                _logger.LogInformation("Mobile API listening on {Endpoints}.",
                    string.Join(", ", bind.Select(a => $"{a}:{_options.Port}")));

                var accepts = listeners.Select(l => AcceptLoopAsync(l, cycle.Token)).ToList();
                // Re-check the address set periodically; rebind when it changes.
                while (!stoppingToken.IsCancellationRequested)
                {
                    await DelaySafe(RebindInterval, stoppingToken);
                    var now = _resolver.FindTailnetAddresses();
                    if (!now.ToHashSet().SetEquals(addresses))
                    {
                        _logger.LogInformation("Tailscale address changed; re-binding the mobile API.");
                        break;
                    }
                }
                cycle.Cancel();
                foreach (var l in listeners) l.Stop();
                await Task.WhenAll(accepts);
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Mobile API could not bind port {Port}; retrying in {Seconds}s.",
                    _options.Port, RebindInterval.TotalSeconds);
                foreach (var l in listeners) l.Stop();
                await DelaySafe(RebindInterval, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A fault here must not stop the Agent host (and with it Telegram monitoring).
                _logger.LogError(ex, "Mobile API failed; restarting it in {Seconds}s.", RebindInterval.TotalSeconds);
                foreach (var l in listeners) l.Stop();
                await DelaySafe(RebindInterval, stoppingToken);
            }
            finally
            {
                lock (_gate) { _listening = false; }
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
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
                try { await HandleConnectionAsync(client, ct); }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or WebSocketException)
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

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        // Defense in depth behind the bind address and firewall rule: tailnet or loopback peers only.
        if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
            !(TailscaleAddressResolver.IsTailnetV4(remote.Address) || IPAddress.IsLoopback(remote.Address)))
            return;

        var stream = client.GetStream();
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

            case (_, "/api/v1/info" or "/api/v1/pair" or "/api/v1/snapshot" or "/api/v1/live"):
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
