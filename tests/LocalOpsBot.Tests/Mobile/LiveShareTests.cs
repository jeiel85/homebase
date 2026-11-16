using System.Net;
using System.Net.Sockets;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>The device page of the Tray: richer device state and live views of other PCs (DECISIONS M-46).</summary>
public class PresenceDetailTests
{
    [Fact]
    public void Display_fields_out_of_range_are_dropped_and_the_rest_kept()
    {
        var report = new PresenceReport(1, DeviceKind.Phone, "0.33.0", Battery: 80, Charging: true, Network: "wifi",
            MemUsedBytes: 5_000, MemTotalBytes: 8_000, UptimeSec: 3600,
            Os: "Android 15", StorageUsedBytes: 300, StorageTotalBytes: 200, BatteryTempC: 31.26, BatteryHealth: "good",
            Plug: "usb", ChargeFullSec: -1, PowerSave: false, Thermal: "none", Signal: 9, LinkMbps: 866, Forwarding: true, Dnd: false);

        Assert.Null(PresenceReport.Validate(report)); // display fields never fail a report
        var s = report.Sanitized().ToStatus();

        Assert.Equal(("Android 15", 31.3, "good", "usb"), (s.Os, s.BatteryTempC, s.BatteryHealth, s.Plug));
        Assert.Null(s.StorageUsedBytes); // used > total
        Assert.Null(s.StorageTotalBytes);
        Assert.Null(s.ChargeFullSec);
        Assert.Null(s.Signal);
        Assert.Equal((866, true, false, "none"), (s.LinkMbps, s.Forwarding, s.Dnd, s.Thermal));
        Assert.Equal((5_000L, 8_000L, 3600L), (s.MemUsedBytes, s.MemTotalBytes, s.UptimeSec));
    }

    [Fact]
    public void The_hub_keeps_the_sanitized_report()
    {
        var time = new ManualTimeProvider();
        var store = new InMemoryDeviceStore();
        var phone = store.Add("Galaxy", "samsung SM-S921N", "h1");
        var hub = new DeviceHub(store, new DeviceStatusBoard(time), new NotificationInbox(time), () => new DeviceStatus(),
            null, "HUB", "0.33.0", NullLogger.Instance, time);

        var answer = hub.Presence(phone, new PresenceReport(1, DeviceKind.Phone, BatteryTempC: 400, Os: "Android 15"));

        var status = Assert.Single(answer.Devices).Status!;
        Assert.Equal("Android 15", status.Os);
        Assert.Null(status.BatteryTempC);
    }

    [Fact]
    public void Local_info_says_which_devices_take_push_and_what_they_muted()
    {
        var store = new InMemoryDeviceStore();
        var phone = store.Add("Galaxy", null, "h1");
        var pc = store.Add("WORK", "Windows", "h2", DeviceKind.Pc);
        store.SetPushSubscription(phone.DeviceId, new LocalOpsBot.Core.Push.PushSubscription("https://ntfy.sh/x", "p", "a",
            DateTimeOffset.UnixEpoch, new[] { "advisor" }));
        var hub = new DeviceHub(store, new DeviceStatusBoard(TimeProvider.System), new NotificationInbox(TimeProvider.System),
            () => new DeviceStatus(), null, "HUB", "0.33.0", NullLogger.Instance);

        var info = hub.LocalInfo().ToDictionary(i => i.DeviceId);

        Assert.True(info[phone.DeviceId].Push);
        Assert.Equal(new[] { "advisor" }, info[phone.DeviceId].Muted);
        Assert.False(info[pc.DeviceId].Push);
    }
}

public class DeviceHubLiveTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly DeviceHub _hub;
    private readonly MobileDeviceInfo _pc;

    public DeviceHubLiveTests()
    {
        _pc = _store.Add("WORK-PC", "Windows", "h1", DeviceKind.Pc);
        _hub = new DeviceHub(_store, new DeviceStatusBoard(_time), new NotificationInbox(_time), () => new DeviceStatus(),
            null, "HUB", "0.33.0", NullLogger.Instance, _time);
    }

    private static LiveSnapshot Snapshot() => ProtocolGoldenTests.SampleSnapshot();

    [Fact]
    public async Task A_waiting_poll_is_woken_when_the_tray_starts_looking()
    {
        var poll = _hub.InboxAsync(_pc, 0, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(poll.IsCompleted);

        _hub.WantLive(_pc.DeviceId);

        var answer = await poll.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(answer!.Live);
    }

    [Fact]
    public async Task A_request_between_polls_answers_the_next_poll_at_once_but_only_once()
    {
        _hub.WantLive(_pc.DeviceId);

        var first = await _hub.InboxAsync(_pc, 0, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first!.Live);

        // Renewing the lease does not make every poll return at once (an old PC would spin).
        _hub.WantLive(_pc.DeviceId);
        var second = _hub.InboxAsync(_pc, first.Next, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public void Shared_snapshots_are_kept_only_while_wanted()
    {
        Assert.False(_hub.AcceptLive(_pc, Snapshot()));
        Assert.Null(_hub.LiveFrame(_pc.DeviceId));

        _hub.WantLive(_pc.DeviceId);
        Assert.True(_hub.AcceptLive(_pc, Snapshot()));
        Assert.NotNull(_hub.LiveFrame(_pc.DeviceId));

        _time.Advance(DeviceHub.LiveLease + TimeSpan.FromSeconds(1));
        Assert.False(_hub.AcceptLive(_pc, Snapshot())); // nobody looks any more: the sender stops
        Assert.Null(_hub.LiveFrame(_pc.DeviceId));
    }

    [Fact]
    public async Task Only_a_linked_pc_may_share_and_only_with_its_token()
    {
        var phone = _store.Add("Galaxy", null, PairingService.HashToken("phone-token"));
        var pc = _store.Add("HOME", "Windows", PairingService.HashToken("pc-token"), DeviceKind.Pc);
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false });
        var api = new MobileApiServer(
            new LiveSnapshotHub(new FakeSampler(), options, _time, NullLogger<LiveSnapshotHub>.Instance),
            new PairingService(_store, _time), _store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance, deviceHub: _hub);
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Snapshot(), MobileProtocol.Json);
        HttpRequestHead Post(string? token) => new("POST", MobileApiServer.LiveSharePath,
            token is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }, null);

        Assert.Equal(401, (await api.HandleRestAsync(Post(null), body, default)).Status);
        Assert.Equal(403, (await api.HandleRestAsync(Post("phone-token"), body, default)).Status);
        var (status, json) = await api.HandleRestAsync(Post("pc-token"), body, default);
        Assert.Equal(200, status);
        Assert.Contains("\"live\":false", json);

        _hub.WantLive(pc.DeviceId);
        Assert.Contains("\"live\":true", (await api.HandleRestAsync(Post("pc-token"), body, default)).Json);
        Assert.Equal(400, (await api.HandleRestAsync(Post("pc-token"), "{\"schema\":1}"u8.ToArray(), default)).Status);
    }
}

public class LiveWatcherKindTests
{
    [Fact]
    public void The_trays_own_view_does_not_count_as_another_device_watching()
    {
        var sampler = new FakeSampler();
        var hub = new LiveSnapshotHub(sampler, Options.Create(new MobileOptions()), TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var remote = new List<bool>();
        var any = new List<bool>();
        hub.RemoteWatchingChanged += remote.Add;
        hub.WatchingChanged += any.Add;

        var local = hub.Subscribe(local: true);
        Assert.True(sampler.Started);
        Assert.Equal((1, 0), (hub.Watchers, hub.RemoteWatchers));
        Assert.Empty(remote);

        var phone = hub.Subscribe();
        Assert.Equal(new[] { true }, remote);
        Assert.True(hub.IsRemoteWatching);

        phone.Dispose();
        Assert.Equal(new[] { true, false }, remote);
        Assert.True(sampler.Started); // the Tray still looks

        local.Dispose();
        Assert.False(sampler.Started);
        Assert.Equal(new[] { true, false }, any);
    }

    [Theory]
    [InlineData("0.33.0", true)]
    [InlineData("0.34.0", false)]
    [InlineData("0.34.1+abc", false)]
    [InlineData("1.0.0-beta", false)]
    public void Pcs_before_v0_34_cannot_share(string version, bool tooOld) =>
        Assert.Equal(tooOld, TrayLiveService.ParseVersion(version) < TrayLiveService.ShareVersion);
}

public class LiveShareEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-live-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Both_pcs_of_a_link_see_each_other_live()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });

        // PC A — the hub. B links to it, so A has no token for B: B shares on request.
        var storeA = new InMemoryDeviceStore();
        var liveA = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var deviceHubA = new DeviceHub(storeA, new DeviceStatusBoard(TimeProvider.System), new NotificationInbox(TimeProvider.System),
            () => new DeviceStatus("0.34.0"), null, "HUB-A", "0.34.0", NullLogger.Instance);
        var api = new MobileApiServer(liveA, new PairingService(storeA, TimeProvider.System), storeA,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance,
            deviceHub: deviceHubA);
        using var trayA = new TrayLiveService(liveA, storeA, TimeProvider.System, NullLogger<TrayLiveService>.Instance, deviceHubA);

        // PC B — linked to A; opens A's live WebSocket with its token.
        var storeB = new InMemoryDeviceStore("pc-b");
        var samplerB = new FakeSampler();
        var liveB = new LiveSnapshotHub(samplerB, options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var links = new HubLinkService(new HubStore(_dir, NullLogger.Instance, protect: false), storeB,
            new SystemStatusProbeAdapter(h => new PresenceReport(1, DeviceKind.Pc, "0.34.0", CpuPct: 7, PushHashes: h)),
            TimeProvider.System, NullLogger<HubLinkService>.Instance, allowLoopback: true, live: liveB);
        using var trayB = new TrayLiveService(liveB, storeB, TimeProvider.System, NullLogger<TrayLiveService>.Instance,
            links: links, allowLoopback: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await api.StartAsync(cts.Token);
        await links.StartAsync(cts.Token);
        try
        {
            var offer = await Until(() => api.CreateOffer(), cts.Token);
            Assert.True((await links.LinkAsync(PairingLink.Build(offer), cts.Token)).Linked);
            var bOnA = Assert.Single(storeA.List());
            await Until(() => deviceHubA.Devices().Devices.FirstOrDefault(d => d.Online), cts.Token);

            // A's Tray looks at B: B is asked through its inbox poll and starts sharing.
            Assert.False(samplerB.Started);
            var shared = await Until(() => trayA.Get(LiveTarget.Device(bOnA.DeviceId)) is { State: LiveViewState.Live } v ? v : null, cts.Token);
            Assert.Equal("shared", shared.Via);
            Assert.Equal(12.5, shared.Snapshot!.System.CpuPct);
            Assert.True(liveB.IsRemoteWatching); // B's Tray shows that another device is viewing it

            // B's Tray looks at A over A's live WebSocket.
            var hubView = await Until(() => trayB.Get(LiveTarget.Hub(storeA.PcId)) is { State: LiveViewState.Live } v ? v : null, cts.Token);
            Assert.Equal(MobileTransport.Loopback, hubView.Via);
            Assert.True(liveA.IsRemoteWatching);

            Assert.Equal(LiveViewState.Unknown, trayA.Get("device:nobody").State);
            Assert.Equal(LiveViewState.Unknown, trayB.Get(LiveTarget.Hub("not-linked")).State);
        }
        finally
        {
            await links.StopAsync(CancellationToken.None);
            await api.StopAsync(CancellationToken.None);
        }
    }

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
}
