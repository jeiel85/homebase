using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>The device group (DECISIONS M-47).</summary>
public class GroupTombstoneTests
{
    [Fact]
    public void Removals_merge_newest_first_and_expire_after_a_week()
    {
        var time = new ManualTimeProvider();
        var t = new GroupTombstones(time);
        t.Add("pc:a");
        var fresh = t.Merge(new[] { new GroupRemoval("pc:a", time.Now.AddMinutes(-1)), new GroupRemoval("pc:b", time.Now), new GroupRemoval("bogus", time.Now) });

        Assert.Equal(new[] { "pc:b" }, fresh);
        Assert.True(t.IsRemoved("pc:a") && t.IsRemoved("pc:b"));
        Assert.Empty(t.Merge(new[] { new GroupRemoval("pc:c", time.Now - GroupTombstones.Keep - TimeSpan.FromHours(1)) }));

        time.Advance(GroupTombstones.Keep + TimeSpan.FromMinutes(1));
        Assert.False(t.IsRemoved("pc:a"));
        Assert.Empty(t.List());
    }

    [Fact]
    public void An_introduction_code_pairs_once_and_leaves_the_qr_code_alone()
    {
        var store = new InMemoryDeviceStore();
        var pairing = new PairingService(store, TimeProvider.System);
        var (qr, _) = pairing.Begin();
        var (intro, _) = pairing.BeginIntroduction();

        Assert.Equal(PairOutcome.Paired, pairing.TryPair(intro, "WORK", "Windows", DeviceKind.Pc).Outcome);
        Assert.Equal(PairOutcome.InvalidCode, pairing.TryPair(intro, "WORK", "Windows", DeviceKind.Pc).Outcome);
        Assert.Equal(PairOutcome.Paired, pairing.TryPair(qr, "Galaxy", "SM-S921N").Outcome);
    }
}

public class DeviceGroupEndToEndTests : IAsyncLifetime
{
    private readonly List<Node> _nodes = new();
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(90));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
        _cts.Dispose();
    }

    [Fact]
    public async Task Linking_one_pc_joins_the_whole_group_and_a_removal_reaches_every_member()
    {
        var a = await Node.StartAsync("pc-a", _cts.Token);
        var b = await Node.StartAsync("pc-b", _cts.Token);
        var c = await Node.StartAsync("pc-c", _cts.Token);
        _nodes.AddRange(new[] { a, b, c });

        // B links to A by hand: A links back, so both hold each other's token.
        Assert.True((await b.Links.LinkAsync(PairingLink.Build(a.Api.CreateOffer()!), _cts.Token)).Linked);
        await Until(() => a.Links.Hub("pc-b"), _cts.Token);

        // C links to A by hand. B, in A's group, links to C on the next sync — nobody typed a code.
        Assert.True((await c.Links.LinkAsync(PairingLink.Build(a.Api.CreateOffer()!), _cts.Token)).Linked);
        await Until(() => a.Links.Hub("pc-c"), _cts.Token);
        await b.Group.SyncAsync(_cts.Token);
        Assert.NotNull(b.Links.Hub("pc-c"));
        await Until(() => c.Links.Hub("pc-b"), _cts.Token);

        var groupOfA = a.Group.Describe();
        Assert.Equal(new[] { "pc-b", "pc-c" }, groupOfA.Pcs.Select(p => p.PcId).OrderBy(x => x));
        Assert.All(groupOfA.Pcs, p => Assert.True(p.Token));

        // Taking C out on B takes it out on A too; the removal travels with A's group answer.
        await b.Group.RemoveAsync(GroupKeys.Pc("pc-c"), fanOut: true, _cts.Token);
        Assert.Null(b.Links.Hub("pc-c"));
        Assert.Null(a.Links.Hub("pc-c"));
        Assert.DoesNotContain(a.Store.List(), d => d.Kind == DeviceKind.Pc && a.Board.PcIdOf(d.DeviceId) == "pc-c");
        Assert.Contains(a.Group.Describe().Removed!, r => r.Key == "pc:pc-c");
        Assert.Empty(c.Links.Hubs()); // C was told too and dropped its own links

        // A member never re-introduces a removed PC.
        Assert.Null(await a.Group.IntroduceAsync("pc-c", _cts.Token));
        Assert.Equal("removed", (await a.Group.JoinAsync(c.Api.CreateOffer(introduction: true)!, _cts.Token)).Error);
    }

    // An endpoint whose PC answers with another pcId (say, of a PC already linked) must not take that
    // PC's place: the real link would be dropped and our pairing there deleted.
    [Fact]
    public async Task An_offer_answered_by_a_different_pc_is_not_linked()
    {
        var a = await Node.StartAsync("pc-a", _cts.Token);
        var b = await Node.StartAsync("pc-b", _cts.Token);
        _nodes.AddRange(new[] { a, b });

        var posing = a.Api.CreateOffer()! with { PcId = "pc-x", PcName = "PC-X" };
        var result = await b.Links.LinkWithOfferAsync(posing, _cts.Token);

        Assert.False(result.Linked);
        Assert.Equal("mismatch", result.Error);
        Assert.Null(b.Links.Hub("pc-a"));
        Assert.Null(b.Links.Hub("pc-x"));
        // The pairing it got on A is handed back rather than left behind.
        await Until(() => a.Store.List().All(d => d.Kind != DeviceKind.Pc) ? "gone" : null, _cts.Token);
    }

    [Fact]
    public async Task An_alias_set_on_a_tray_or_a_phone_reaches_every_member()
    {
        var a = await Node.StartAsync("pc-a", _cts.Token);
        var b = await Node.StartAsync("pc-b", _cts.Token);
        _nodes.AddRange(new[] { a, b });
        Assert.True((await b.Links.LinkAsync(PairingLink.Build(a.Api.CreateOffer()!), _cts.Token)).Linked);
        await Until(() => a.Links.Hub("pc-b"), _cts.Token);

        // Named on A's tray (M-51): A answers at once and hands it to B in the background.
        Assert.True(a.Group.SetAlias("pc:pc-b", "PC-B", "작업실"));
        Assert.False(a.Group.SetAlias("pc:pc-b", "PC-B", "작업실"));
        Assert.Equal("작업실", await Until(() => b.Group.Aliases.Get("pc:pc-b"), _cts.Token));

        // Named on a phone paired with B only: A takes it from B's group answer on its next sync.
        var pairing = new PairingService(b.Store, TimeProvider.System);
        var phone = pairing.TryPair(pairing.Begin().Code, "Galaxy S24", "SM-S921N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new GroupAliasRequest(MobileProtocol.Schema,
            new[] { new DeviceAlias("pc:pc-a", "PC-A", "거실", DateTimeOffset.UtcNow) }), MobileProtocol.Json);
        var (status, json) = await b.Api.HandleRestAsync(Post(GroupPaths.Alias, phone.Token), body, _cts.Token);
        Assert.Equal(200, status);
        Assert.Equal(1, JsonSerializer.Deserialize<GroupAliasResponse>(json, MobileProtocol.Json)!.Changed);
        Assert.Null(a.Group.Aliases.Get("pc:pc-a"));
        await a.Group.SyncAsync(_cts.Token);
        Assert.Equal("거실", a.Group.Aliases.Get("pc:pc-a"));
        Assert.Contains(a.Group.Describe().Aliases!, e => e is { Key: "pc:pc-b", Alias: "작업실" });

        // Only paired devices may name things, with a well-formed body.
        Assert.Equal(401, (await b.Api.HandleRestAsync(Post(GroupPaths.Alias, "not-a-token"), body, _cts.Token)).Item1);
        Assert.Equal(400, (await b.Api.HandleRestAsync(Post(GroupPaths.Alias, phone.Token), "{}"u8.ToArray(), _cts.Token)).Item1);
    }

    private static HttpRequestHead Post(string path, string? token) =>
        new("POST", path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token });

    private static async Task<T> Until<T>(Func<T?> probe, CancellationToken ct) where T : class
    {
        while (true)
        {
            if (probe() is { } value) return value;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>One PC's Agent pieces on a loopback port.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly string _dir;

        private Node(string dir, InMemoryDeviceStore store, DeviceStatusBoard board, MobileApiServer api, HubLinkService links, DeviceGroupService group)
        {
            _dir = dir;
            Store = store;
            Board = board;
            Api = api;
            Links = links;
            Group = group;
        }

        public InMemoryDeviceStore Store { get; }
        public DeviceStatusBoard Board { get; }
        public MobileApiServer Api { get; }
        public HubLinkService Links { get; }
        public DeviceGroupService Group { get; }

        public static async Task<Node> StartAsync(string pcId, CancellationToken ct)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hb-group-" + Guid.NewGuid().ToString("N"));
            var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = FreePort() });
            var store = new InMemoryDeviceStore(pcId);
            var board = new DeviceStatusBoard(TimeProvider.System);
            var live = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
            var hub = new DeviceHub(store, board, new NotificationInbox(TimeProvider.System), () => new DeviceStatus("0.35.0"),
                null, pcId, "0.35.0", NullLogger.Instance);
            var api = new MobileApiServer(live, new PairingService(store, TimeProvider.System), store,
                new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
                new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance, deviceHub: hub);
            var links = new HubLinkService(new HubStore(dir, NullLogger.Instance, protect: false), store,
                new SystemStatusProbeAdapter(h => new PresenceReport(1, DeviceKind.Pc, "0.35.0", PushHashes: h)),
                TimeProvider.System, NullLogger<HubLinkService>.Instance, allowLoopback: true, live: live);
            var group = new DeviceGroupService(links, store, board, new GroupTombstones(TimeProvider.System),
                () => api.CreateOffer(introduction: true), pcId, TimeProvider.System, NullLogger<DeviceGroupService>.Instance);
            api.AttachGroup(group);
            await api.StartAsync(ct);
            await links.StartAsync(ct);
            await Until(() => api.CreateOffer(introduction: true), ct);
            return new Node(dir, store, board, api, links, group);
        }

        public async ValueTask DisposeAsync()
        {
            await Links.StopAsync(CancellationToken.None);
            await Api.StopAsync(CancellationToken.None);
            Group.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
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
}
