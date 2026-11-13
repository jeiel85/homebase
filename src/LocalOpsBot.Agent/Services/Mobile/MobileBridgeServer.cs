using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Agent side of <c>\\.\pipe\Homebase.MobileBridge</c>. The Tray reports windows/Claude sessions
/// (only after the Agent says a phone is watching) and asks for pairing QR offers and the device
/// list. ACL: SYSTEM, Administrators and the configured user (else INTERACTIVE) — narrower than the
/// notification pipe's Authenticated Users (Device Hub R-04). Unknown ops close the connection.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MobileBridgeServer : BackgroundService
{
    private const int MaxInstances = 4;

    private readonly LiveSnapshotHub _hub;
    private readonly MobileApiServer _api;
    private readonly IMobileDeviceStore _devices;
    private readonly MobileOptions _options;
    private readonly ILogger<MobileBridgeServer> _logger;
    private readonly object _gate = new();
    private readonly List<Connection> _connections = new();

    private sealed class Connection(Stream stream)
    {
        public Stream Stream { get; } = stream;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    public MobileBridgeServer(
        LiveSnapshotHub hub,
        MobileApiServer api,
        IMobileDeviceStore devices,
        IOptions<MobileOptions> options,
        ILogger<MobileBridgeServer> logger)
    {
        _hub = hub;
        _api = api;
        _devices = devices;
        _options = options.Value;
        _logger = logger;
        _hub.WatchingChanged += OnWatchingChanged;
    }

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
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, BuildSecurity());
            }
            catch (IOException)
            {
                // All instances busy — wait for one to free up.
                await Task.Delay(1000, stoppingToken).ContinueWith(_ => { });
                running.RemoveAll(t => t.IsCompleted);
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

            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => ServeAsync(pipe, stoppingToken), CancellationToken.None));
        }
        await Task.WhenAll(running);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var conn = new Connection(pipe);
        lock (_gate) _connections.Add(conn);
        try
        {
            await SendAsync(conn, MobileBridge.Message(MobileBridge.OpWatch, new { on = _hub.IsWatching }), ct);
            while (!ct.IsCancellationRequested)
            {
                var msg = await MobileBridge.ReadFrameAsync(pipe, ct);
                if (msg is null) break;
                var op = (string?)msg["op"];
                if (op is null || !MobileBridge.AllowedTrayOps.Contains(op))
                {
                    _logger.LogWarning("Mobile bridge: rejected op '{Op}'; closing the connection.", op);
                    break;
                }
                await HandleAsync(conn, op, (string?)msg["id"], msg["payload"], ct);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug("Mobile bridge connection ended: {Message}", ex.Message);
        }
        finally
        {
            lock (_gate) _connections.Remove(conn);
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

        if (id is null) return; // requests need an id to answer
        JsonObject response = op switch
        {
            MobileBridge.OpPairingBegin => _api.CreateOffer() is { } offer
                ? MobileBridge.Response(id, true, offer)
                : MobileBridge.Response(id, false, error: "not_listening"),
            MobileBridge.OpDevicesList => MobileBridge.Response(id, true, _devices.List()),
            MobileBridge.OpDevicesRevoke => MobileBridge.Response(id,
                _devices.Revoke((string?)payload?["deviceId"] ?? "")),
            MobileBridge.OpStatus => MobileBridge.Response(id, true, _api.Status()),
            _ => MobileBridge.Response(id, false, error: "unsupported"),
        };
        await SendAsync(conn, response, ct);
    }

    private void OnWatchingChanged(bool on)
    {
        List<Connection> conns;
        lock (_gate) conns = _connections.ToList();
        foreach (var c in conns)
        {
            _ = SendAsync(c, MobileBridge.Message(MobileBridge.OpWatch, new { on }), CancellationToken.None)
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

    private PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        void Allow(SecurityIdentifier sid) => security.AddAccessRule(
            new PipeAccessRule(sid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        SecurityIdentifier? user = null;
        if (!string.IsNullOrWhiteSpace(_options.UserSid))
        {
            try { user = new SecurityIdentifier(_options.UserSid); }
            catch (ArgumentException) { _logger.LogWarning("mobile:userSid '{Sid}' is not a valid SID; using INTERACTIVE.", _options.UserSid); }
        }
        Allow(user ?? new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        return security;
    }

    public override void Dispose()
    {
        _hub.WatchingChanged -= OnWatchingChanged;
        base.Dispose();
    }
}
