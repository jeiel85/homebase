using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Real loopback socket: a phone revoked in the Tray loses an already-open live view.</summary>
public class LiveRevocationTests
{
    [Fact]
    public async Task Revoking_a_device_closes_its_open_live_socket()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port, LiveIntervalMs = 500 });
        var store = new InMemoryDeviceStore();
        var pairing = new PairingService(store, TimeProvider.System);
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var api = new MobileApiServer(hub, pairing, store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance);

        var (code, _) = pairing.Begin();
        var paired = pairing.TryPair(code, "Phone", null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await api.StartAsync(cts.Token);
        try
        {
            using var ws = await ConnectWithRetryAsync(new Uri($"ws://127.0.0.1:{port}/api/v1/live"), paired.Token!, cts.Token);

            var buf = new byte[256 * 1024];
            var first = await ws.ReceiveAsync(buf, cts.Token);
            Assert.Equal(WebSocketMessageType.Text, first.MessageType);

            Assert.True(store.Revoke(paired.DeviceId!));

            WebSocketReceiveResult r;
            do { r = await ws.ReceiveAsync(buf, cts.Token); } while (r.MessageType == WebSocketMessageType.Text);

            Assert.Equal(WebSocketMessageType.Close, r.MessageType);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, ws.CloseStatus);
            Assert.Equal(MobileApiServer.RevokedCloseReason, ws.CloseStatusDescription);
        }
        finally
        {
            await api.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<ClientWebSocket> ConnectWithRetryAsync(Uri uri, string token, CancellationToken ct)
    {
        // The listener binds asynchronously in ExecuteAsync; a ClientWebSocket can't be reused after
        // a failed connect, so retry with a fresh one until the server accepts.
        for (var i = 0; ; i++)
        {
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
            try
            {
                await ws.ConnectAsync(uri, ct);
                return ws;
            }
            catch (WebSocketException) when (i < 50)
            {
                ws.Dispose();
                await Task.Delay(100, ct);
            }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
