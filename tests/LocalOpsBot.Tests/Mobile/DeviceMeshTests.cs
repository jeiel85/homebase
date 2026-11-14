using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Agent.Services;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class DeviceStatusBoardTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void A_device_is_online_for_two_minutes_after_its_report_and_pcs_are_listed_first()
    {
        var board = new DeviceStatusBoard(_time);
        var phone = new MobileDeviceInfo("p1", "Galaxy", "SM-S921N", _time.GetUtcNow(), null);
        var pc = new MobileDeviceInfo("c1", "WORK-PC", "Windows", _time.GetUtcNow(), null, DeviceKind.Pc);
        board.Report("p1", new PresenceReport(1, DeviceKind.Phone, "0.22.0", Battery: 81, Charging: true));

        var entries = board.Entries(new[] { phone, pc });
        Assert.Equal(new[] { "c1", "p1" }, entries.Select(e => e.DeviceId));
        Assert.False(entries[0].Online);
        Assert.Null(entries[0].Status);
        Assert.True(entries[1].Online);
        Assert.Equal(81, entries[1].Status!.Battery);

        _time.Advance(DeviceStatusBoard.OnlineWindow + TimeSpan.FromSeconds(1));
        Assert.False(board.Entries(new[] { phone })[0].Online);
    }

    [Theory]
    [InlineData(101, null, "Battery")]
    [InlineData(null, -1.0, "CPU")]
    public void Out_of_range_reports_are_rejected(int? battery, double? cpu, string expect) =>
        Assert.Contains(expect, PresenceReport.Validate(new PresenceReport(1, DeviceKind.Phone, Battery: battery, CpuPct: cpu)));

    [Fact]
    public void Push_hashes_must_be_sha256_hex() =>
        Assert.NotNull(PresenceReport.Validate(new PresenceReport(1, DeviceKind.Pc, PushHashes: ["https://ntfy.sh/up"])));
}

public class NotificationInboxTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void A_reader_gets_what_is_new_except_what_it_sent_itself()
    {
        var inbox = new NotificationInbox(_time);
        inbox.Add(NotificationInbox.SelfOrigin, "HOME-PC", "Slack", "a", null, _time.GetUtcNow());
        inbox.Add("pcB", "WORK-PC", "Teams", "b", null, _time.GetUtcNow());
        inbox.Add("phone1", "Galaxy", "카카오톡", "c", "body", _time.GetUtcNow());

        var forB = inbox.Read(0, "pcB");
        Assert.Equal(new[] { "a", "c" }, forB.Items.Select(i => i.Title));
        Assert.Equal(3, forB.Next);
        Assert.Empty(inbox.Read(forB.Next, "pcB").Items);
    }

    [Fact]
    public void A_cursor_from_before_a_hub_restart_starts_over()
    {
        var inbox = new NotificationInbox(_time);
        inbox.Add(NotificationInbox.SelfOrigin, "HOME-PC", "Slack", "only", null, _time.GetUtcNow());
        Assert.Equal("only", Assert.Single(inbox.Read(500, "pcB").Items).Title);
    }

    [Fact]
    public void Old_items_are_not_handed_out()
    {
        var inbox = new NotificationInbox(_time);
        inbox.Add(NotificationInbox.SelfOrigin, "HOME-PC", "Slack", "old", null, _time.GetUtcNow());
        _time.Advance(NotificationInbox.Retention + TimeSpan.FromMinutes(1));
        Assert.Empty(inbox.Read(0, "pcB").Items);
    }

    [Fact]
    public async Task A_waiting_poll_returns_as_soon_as_something_arrives()
    {
        var inbox = new NotificationInbox(TimeProvider.System);
        var wait = inbox.WaitAsync(0, "pcB", TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);

        inbox.Add(NotificationInbox.SelfOrigin, "HOME-PC", "Slack", "now", null, DateTimeOffset.UtcNow);

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("now", Assert.Single(result.Items).Title);
    }
}

public class RelayBundlingTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void The_same_app_on_two_devices_bundles_separately_and_keeps_its_origin()
    {
        var bundler = new ToastBundler(TimeSpan.FromSeconds(60), 100, _time);
        var phone = new ToastOrigin("p1", "Galaxy", false, Array.Empty<string>());

        Assert.NotNull(bundler.Add(new ToastItem("카카오톡", "pc", null, _time.GetUtcNow())));
        var relayed = bundler.Add(new ToastItem("카카오톡", "phone", null, _time.GetUtcNow(), phone));

        Assert.NotNull(relayed);
        Assert.Same(phone, relayed!.Origin);
        Assert.Equal("카카오톡", relayed.SourceApp);
    }

    [Fact]
    public void A_relayed_batch_is_labelled_with_its_device_and_carries_the_skip_list()
    {
        var origin = new ToastOrigin("p1", "Galaxy S24", false, new[] { new string('a', 64) });
        var batch = new ToastBatch("카카오톡", new[] { new ToastItem("카카오톡", "엄마", "밥 먹었니?", _time.GetUtcNow(), origin) }, 1);

        var n = NotificationForwardingService.ToOutbound(batch, telegramFallback: false, _time.GetUtcNow());

        Assert.Equal(NotificationForwardingService.DeviceCategory, n.Category);
        Assert.Equal("Galaxy S24 · 카카오톡: 엄마", n.Title);
        Assert.Equal("p1", n.Metadata![PushRelay.OriginDeviceKey]);
        Assert.Equal(new string('a', 64), n.Metadata![PushRelay.SkipHashesKey]);
        Assert.Equal(DeliveryPolicy.EncryptedOnly, n.Policy);
    }
}

public class DeviceHubApiTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly PairingService _pairing;
    private readonly List<(ToastOrigin Origin, DeviceNotification N)> _relayed = new();

    public DeviceHubApiTests() => _pairing = new PairingService(_store, _time);

    private MobileApiServer Api(bool relay = true)
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var deviceHub = new DeviceHub(_store, new DeviceStatusBoard(_time), new NotificationInbox(_time),
            () => new DeviceStatus("0.22.0", CpuPct: 12.5), relay ? (o, n) => { _relayed.Add((o, n)); return Task.CompletedTask; } : null,
            "HOME-PC", "0.22.0", NullLogger.Instance);
        return new MobileApiServer(hub, _pairing, _store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options,
            NullLogger<MobileApiServer>.Instance, deviceHub: deviceHub);
    }

    private string Pair(string name, string? kind = null)
    {
        var (code, _) = _pairing.Begin();
        return _pairing.TryPair(code, name, null, kind).Token!;
    }

    private static HttpRequestHead Req(string method, string path, string? token = null, string? query = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return new HttpRequestHead(method, path, headers, query);
    }

    private static byte[] Body<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, MobileProtocol.Json);

    [Fact]
    public async Task A_device_reports_its_state_and_gets_every_device_back()
    {
        var api = Api();
        var phone = Pair("Galaxy");
        Pair("WORK-PC", DeviceKind.Pc);

        var (status, json) = await api.HandleRestAsync(Req("POST", "/api/v1/presence", phone),
            Body(new PresenceReport(1, DeviceKind.Phone, "0.22.0", Battery: 64, Network: "wifi")), default);

        Assert.Equal(200, status);
        var devices = JsonSerializer.Deserialize<DevicesResponse>(json, MobileProtocol.Json)!;
        Assert.Equal("HOME-PC", devices.Hub.PcName);
        Assert.Equal(12.5, devices.Hub.Status.CpuPct);
        Assert.Equal(new[] { DeviceKind.Pc, DeviceKind.Phone }, devices.Devices.Select(d => d.Kind));
        var me = devices.Devices.Single(d => d.Name == "Galaxy");
        Assert.True(me.Online);
        Assert.Equal(64, me.Status!.Battery);
    }

    [Fact]
    public async Task Every_new_route_needs_the_token()
    {
        var api = Api();
        foreach (var (method, path) in new[] { ("POST", "/api/v1/presence"), ("GET", "/api/v1/devices"),
                     ("POST", "/api/v1/notifications"), ("GET", "/api/v1/inbox"), ("DELETE", "/api/v1/device") })
        {
            var (status, _) = await api.HandleRestAsync(Req(method, path, "nope"), Encoding.UTF8.GetBytes("{}"), default);
            Assert.Equal(401, status);
        }
    }

    [Fact]
    public async Task A_notification_is_relayed_with_its_sender_as_origin()
    {
        var api = Api();
        var token = Pair("Galaxy");

        var (status, _) = await api.HandleRestAsync(Req("POST", "/api/v1/notifications", token),
            Body(new DeviceNotification(1, "n1", "카카오톡", "엄마", "밥 먹었니?", DateTimeOffset.UtcNow)), default);

        Assert.Equal(202, status);
        // Relayed in the background so the sender isn't held up by the push to other phones.
        for (var i = 0; i < 50 && _relayed.Count == 0; i++) await Task.Delay(20);
        var (origin, n) = Assert.Single(_relayed);
        Assert.Equal("Galaxy", origin.Name);
        Assert.False(origin.FromHub);
        Assert.Equal("n1", n.Id);
    }

    [Fact]
    public async Task Without_notification_forwarding_the_hub_says_so()
    {
        var api = Api(relay: false);
        var token = Pair("Galaxy");
        var (status, json) = await api.HandleRestAsync(Req("POST", "/api/v1/notifications", token),
            Body(new DeviceNotification(1, "n1", "앱", "t", null, DateTimeOffset.UtcNow)), default);
        Assert.Equal(503, status);
        Assert.Contains("forwarding_off", json);
    }

    [Fact]
    public async Task An_oversized_notification_is_rejected()
    {
        var api = Api();
        var token = Pair("Galaxy");
        var (status, _) = await api.HandleRestAsync(Req("POST", "/api/v1/notifications", token),
            Body(new DeviceNotification(1, "n1", "앱", "t", new string('x', DeviceNotification.MaxBody + 1), DateTimeOffset.UtcNow)), default);
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task A_device_can_unpair_itself()
    {
        var api = Api();
        var token = Pair("Galaxy");
        var (status, _) = await api.HandleRestAsync(Req("DELETE", "/api/v1/device", token), null, default);
        Assert.Equal(200, status);
        Assert.Empty(_store.List());
    }

    [Fact]
    public void Pairing_as_a_pc_is_remembered()
    {
        Pair("WORK-PC", DeviceKind.Pc);
        Pair("", null);
        Assert.Equal(new[] { DeviceKind.Pc, DeviceKind.Phone }, _store.List().Select(d => d.Kind));
        Assert.Equal("Android", _store.List()[1].Name);
    }
}

/// <summary>
/// Two Agents on loopback (DECISIONS M-29): B links to A with A's pairing link, then they exchange
/// state and notifications, and nothing comes back around.
/// </summary>
public class HubLinkEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-hubs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_linked_pc_exchanges_state_and_notifications_with_its_hub()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });

        // PC A — the hub.
        var storeA = new InMemoryDeviceStore();
        var pairingA = new PairingService(storeA, TimeProvider.System);
        var routerA = new RecordingRouter();
        var toastsA = new RecordingToaster();
        var inboxA = new NotificationInbox(TimeProvider.System);
        var forwardingA = Forwarding(routerA, inboxA, toastsA);
        var boardA = new DeviceStatusBoard(TimeProvider.System);
        var deviceHubA = new DeviceHub(storeA, boardA, inboxA, () => new DeviceStatus("0.22.0", CpuPct: 5),
            (o, n) => forwardingA.AcceptFromDeviceAsync(o, n.App, n.Title, n.Body, n.Ts, n.Id), "HUB-A", "0.22.0", NullLogger.Instance);
        var api = new MobileApiServer(
            new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            pairingA, storeA, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance,
            deviceHub: deviceHubA);

        // PC B — links to A.
        var storeB = new InMemoryDeviceStore("pc-b");
        var routerB = new RecordingRouter();
        var toastsB = new RecordingToaster();
        var forwardingB = Forwarding(routerB, new NotificationInbox(TimeProvider.System), toastsB);
        var links = new HubLinkService(new HubStore(_dir, NullLogger.Instance, protect: false), storeB,
            new SystemStatusProbeAdapter(h => new PresenceReport(1, DeviceKind.Pc, "0.22.0", CpuPct: 42, UptimeSec: 3600, PushHashes: h)),
            TimeProvider.System, NullLogger<HubLinkService>.Instance, forwardingB, allowLoopback: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await api.StartAsync(cts.Token);
        await links.StartAsync(cts.Token);
        try
        {
            var offer = await Until(() => api.CreateOffer(), cts.Token);
            var result = await links.LinkAsync(PairingLink.Build(offer), cts.Token);
            Assert.True(result.Linked, result.Message);
            Assert.Equal(DeviceKind.Pc, Assert.Single(storeA.List()).Kind);

            // State flows both ways: A lists B with B's CPU; B's view of A carries A's CPU.
            await Until(() => deviceHubA.Devices().Devices.FirstOrDefault(d => d.Online && d.Status?.CpuPct == 42), cts.Token);
            var view = await Until(() => links.Views().FirstOrDefault(v => v.Online), cts.Token);
            Assert.Equal("HUB-A", view.PcName);
            Assert.Equal(5, view.Status!.CpuPct);

            // B's own notification reaches A: shown there and pushed to A's phones as relayed.
            await forwardingB.AcceptAsync(new ToastNotificationEvent("e1", "Slack", "deploy done", "all green",
                DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal));
            var atA = await Until(() => routerA.Snapshot().FirstOrDefault(n => n.Category == NotificationForwardingService.DeviceCategory), cts.Token);
            Assert.EndsWith("· Slack: deploy done", atA.Title);
            Assert.Contains(toastsA.Snapshot(), t => t.Title.EndsWith("· Slack"));

            // A's own notification reaches B through the inbox, labelled with A's name.
            await forwardingA.AcceptAsync(new ToastNotificationEvent("e2", "Outlook", "회의 10분 전", null,
                DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal));
            var atB = await Until(() => routerB.Snapshot().FirstOrDefault(n => n.Title.StartsWith("HUB-A · Outlook")), cts.Token);
            Assert.Equal(NotificationForwardingService.DeviceCategory, atB.Category);
            Assert.Contains(toastsB.Snapshot(), t => t.Title == "HUB-A · Outlook");

            // Nothing comes back around: A never receives its own Outlook notification from B.
            await Task.Delay(1500);
            Assert.DoesNotContain(routerA.Snapshot(), n => n.Category == NotificationForwardingService.DeviceCategory && n.Title.Contains("Outlook"));

            // Unlinking removes B from A.
            Assert.True(await links.UnlinkAsync(view.PcId));
            Assert.Empty(storeA.List());
            Assert.Empty(links.Views());
        }
        finally
        {
            await links.StopAsync(CancellationToken.None);
            await api.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_bad_or_own_link_is_refused_without_pairing()
    {
        var store = new InMemoryDeviceStore();
        var links = new HubLinkService(new HubStore(_dir, NullLogger.Instance, protect: false), store,
            new SystemStatusProbeAdapter(_ => new PresenceReport(1, DeviceKind.Pc)), TimeProvider.System,
            NullLogger<HubLinkService>.Instance, allowLoopback: true);

        Assert.Equal("invalid_link", (await links.LinkAsync("https://example.com/#abc", default)).Error);
        var own = new PairingOffer(1, store.PcId, "ME", Array.Empty<string>(), 1, "code", DateTimeOffset.UtcNow.AddMinutes(5),
            new[] { new MobileEndpoint(MobileTransport.Loopback, "127.0.0.1", 1, false) });
        Assert.Equal("self", (await links.LinkAsync(PairingLink.Build(own), default)).Error);
    }

    private static NotificationForwardingService Forwarding(IOutboundRouter router, NotificationInbox inbox, ITrayToaster toaster) => new(
        new NullBridge(), router, new PassThroughMasker(), new ToastForwardingOptions(60, 150, false),
        NullLogger<NotificationForwardingService>.Instance, TimeProvider.System, inbox, toaster);

    private static async Task<T> Until<T>(Func<T?> probe, CancellationToken ct) where T : class
    {
        while (true)
        {
            if (probe() is { } value) return value;
            await Task.Delay(100, ct);
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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    internal sealed class RecordingRouter : IOutboundRouter
    {
        private readonly List<OutboundNotification> _sent = new();

        public List<OutboundNotification> Snapshot() { lock (_sent) return _sent.ToList(); }

        public Task<OutboundDeliveryResult> DeliverAsync(OutboundNotification notification, CancellationToken ct)
        {
            lock (_sent) _sent.Add(notification);
            return Task.FromResult(new OutboundDeliveryResult(true, [new OutboundAttemptResult("unifiedpush", true)]));
        }
    }

    internal sealed class RecordingToaster : ITrayToaster
    {
        private readonly List<TrayToast> _shown = new();

        public List<TrayToast> Snapshot() { lock (_shown) return _shown.ToList(); }

        public void Show(string title, string? body) { lock (_shown) _shown.Add(new TrayToast(title, body)); }
    }

    private sealed class NullBridge : INotificationBridgeServer
    {
#pragma warning disable CS0067
        public event Action<ToastNotificationEvent>? NotificationReceived;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PassThroughMasker : ITextMasker
    {
        public string Mask(string input) => input;
    }
}
