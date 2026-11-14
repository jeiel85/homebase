using System.Net;
using System.Net.Sockets;
using LocalOpsBot.Agent.Services;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class ConnectionCodeTests
{
    private const string Pin = "3f6c1b1e8f0d4a7b9c2e5d8a1b4c7e0f3a6d9c2b5e8f1a4d7c0b3e6f9a2d5c8b";

    private static PairingOffer Offer(string pcId = "6f1c0d2e9b8a4c7d", string? code = null) => new(1, pcId, "HOME-PC",
        Array.Empty<string>(), 47820, code ?? PairingService.Base64Url(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray()),
        DateTimeOffset.UtcNow.AddMinutes(5), new[]
        {
            new MobileEndpoint(MobileTransport.Lan, "192.168.45.101", 47821, true, Pin),
            new MobileEndpoint(MobileTransport.Tailscale, "100.101.102.103", 47821, false),
        });

    [Fact]
    public void Matches_the_official_bip39_vectors()
    {
        // Trezor test vectors (bitcoin/bips BIP-0039), 256-bit entropy.
        Assert.Equal(string.Join(' ', Enumerable.Repeat("abandon", 23).Append("art")), ConnectionCode.EncodeBytes(new byte[32]));
        Assert.Equal(string.Join(' ', Enumerable.Repeat("zoo", 23).Append("vote")),
            ConnectionCode.EncodeBytes(Enumerable.Repeat((byte)0xff, 32).ToArray()));
        Assert.Equal(2048, ConnectionCode.WordList.Count);
    }

    [Fact]
    public void A_code_round_trips_what_another_pc_needs()
    {
        var offer = Offer();
        var words = ConnectionCode.Encode(offer)!;
        Assert.Equal(24, words.Split(' ').Length);

        var data = ConnectionCode.TryDecode(words)!;

        Assert.True(data.MatchesPcId("6f1c0d2e9b8a4c7d"));
        Assert.False(data.MatchesPcId("someone-else"));
        Assert.True(data.MatchesPin(Pin));
        Assert.False(data.MatchesPin(new string('0', 64)));
        Assert.Equal(47821, data.Port);
        Assert.Equal(offer.Code, data.PairingCode);
    }

    [Fact]
    public void Pasted_text_may_be_numbered_upper_case_and_wrapped()
    {
        var words = ConnectionCode.Encode(Offer())!.Split(' ');
        var messy = string.Join("\r\n", Enumerable.Range(0, 4).Select(row =>
            string.Join("  ", Enumerable.Range(row * 6, 6).Select(i => $"{i + 1,2}. {words[i].ToUpperInvariant()}"))));
        Assert.NotNull(ConnectionCode.TryDecode(messy));
    }

    [Fact]
    public void A_wrong_or_missing_word_is_refused()
    {
        var words = ConnectionCode.Encode(Offer())!.Split(' ');
        Assert.Null(ConnectionCode.TryDecode(string.Join(' ', words.Take(23))));
        var swapped = (string[])words.Clone();
        (swapped[3], swapped[4]) = (swapped[4], swapped[3]);
        if (swapped[3] != swapped[4]) Assert.Null(ConnectionCode.TryDecode(string.Join(' ', swapped)));
        Assert.Null(ConnectionCode.TryDecode(string.Join(' ', words.Take(23).Append("notaword"))));
        Assert.Null(ConnectionCode.TryDecode("https://jeiel85.github.io/homebase/pair/#abc"));
    }

    [Fact]
    public void Without_a_lan_path_the_code_carries_no_pin()
    {
        var offer = Offer() with { Endpoints = new[] { new MobileEndpoint(MobileTransport.Tailscale, "100.1.2.3", 47820, false) } };
        var data = ConnectionCode.TryDecode(ConnectionCode.Encode(offer))!;
        Assert.Null(data.LanPinPrefix);
        Assert.Equal(47820, data.Port);
    }

    [Theory]
    [InlineData("192.168.45.101", 24, 253)]   // the /24 minus network, broadcast and itself
    [InlineData("10.0.5.20", 16, 253)]        // a wider subnet is cut to this PC's /24
    [InlineData("192.168.1.10", 30, 1)]       // a /30 has two hosts, one of them is us
    public void Lan_search_stays_within_this_pcs_slash_24(string ip, int prefix, int expected)
    {
        var hosts = HubFinder.LanHosts(new LanAddress(IPAddress.Parse(ip), prefix)).ToList();
        Assert.Equal(expected, hosts.Count);
        Assert.DoesNotContain(IPAddress.Parse(ip), hosts);
        var self = IPAddress.Parse(ip).GetAddressBytes();
        Assert.All(hosts, h => Assert.Equal(self[..3], h.GetAddressBytes()[..3]));
    }

    [Fact]
    public void Online_tailnet_peers_are_read_from_tailscale_status()
    {
        const string json = """
            {"Self":{"DNSName":"me.ts.net."},"Peer":{
              "a":{"Online":true,"TailscaleIPs":["100.118.80.77","fd7a:115c:a1e0::eb37:504e"]},
              "b":{"Online":false,"TailscaleIPs":["100.91.54.15"]},
              "c":{"Online":true,"TailscaleIPs":["100.103.212.64"]}}}
            """;
        Assert.Equal(new[] { "100.118.80.77", "100.103.212.64" },
            TailscaleAddressResolver.ParsePeerAddresses(json).Select(a => a.ToString()));
    }
}

/// <summary>DECISIONS M-30 end to end on loopback: the 24 words alone link PC B to PC A.</summary>
public class ConnectionCodeEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-code-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_words_alone_find_the_pc_and_link_to_it()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });
        var storeA = new InMemoryDeviceStore("pc-a");
        var pairingA = new PairingService(storeA, TimeProvider.System);
        var api = new MobileApiServer(
            new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            pairingA, storeA, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance,
            deviceHub: new DeviceHub(storeA, new DeviceStatusBoard(TimeProvider.System), new NotificationInbox(TimeProvider.System),
                () => new DeviceStatus("0.24.0"), null, "HUB-A", "0.24.0", NullLogger.Instance));

        // Stand-in for the LAN/tailnet sweep: a few dead addresses and the hub's loopback path.
        var finder = new HubFinder(code => new[]
        {
            new MobileEndpoint(MobileTransport.Loopback, "127.0.0.1", FreePort(), false),
            new MobileEndpoint(MobileTransport.Loopback, "127.0.0.1", code.Port, false),
        }, NullLogger.Instance);
        var links = new HubLinkService(new HubStore(_dir, NullLogger.Instance, protect: false), new InMemoryDeviceStore("pc-b"),
            new SystemStatusProbeAdapter(h => new PresenceReport(1, DeviceKind.Pc, "0.24.0", PushHashes: h)),
            TimeProvider.System, NullLogger<HubLinkService>.Instance, allowLoopback: true, finder: finder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await api.StartAsync(cts.Token);
        await links.StartAsync(cts.Token);
        try
        {
            PairingOffer? offer = null;
            while ((offer = api.CreateOffer()) is null) await Task.Delay(100, cts.Token);
            var words = ConnectionCode.Encode(offer)!;

            var result = await links.LinkAsync(words, cts.Token);

            Assert.True(result.Linked, result.Message);
            Assert.Equal(DeviceKind.Pc, Assert.Single(storeA.List()).Kind);
            while (!links.Views().Any(v => v.Online)) await Task.Delay(100, cts.Token);
            Assert.Equal("HUB-A", links.Views().Single().PcName);

            // Single use, like the QR: the same words can't pair a second time.
            var again = await links.LinkAsync(words, cts.Token);
            Assert.False(again.Linked);
        }
        finally
        {
            await links.StopAsync(CancellationToken.None);
            await api.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Another_service_answering_on_the_port_does_not_break_the_search()
    {
        // Something else on a probed address answers 200 with JSON that has no pcId.
        var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var otherPort = ((IPEndPoint)other.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var c = await other.AcceptTcpClientAsync(cts.Token);
                var s = c.GetStream();
                await s.ReadAsync(new byte[4096], cts.Token);
                await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray(), cts.Token);
            }
        });
        try
        {
            var code = ConnectionCode.TryDecode(ConnectionCode.Encode(new PairingOffer(1, "pc-z", "Z", Array.Empty<string>(), 1,
                PairingService.Base64Url(new byte[16]), DateTimeOffset.UtcNow.AddMinutes(5),
                new[] { new MobileEndpoint(MobileTransport.Tailscale, "100.1.2.3", otherPort, false) })))!;
            var finder = new HubFinder(_ => new[] { new MobileEndpoint(MobileTransport.Loopback, "127.0.0.1", otherPort, false) },
                NullLogger.Instance);

            Assert.Null(await finder.FindAsync(code, cts.Token)); // no exception, simply not found
        }
        finally
        {
            cts.Cancel();
            other.Stop();
        }
    }

    [Fact]
    public async Task Own_code_and_mistyped_words_get_clear_answers()
    {
        var store = new InMemoryDeviceStore("pc-a");
        var links = new HubLinkService(new HubStore(_dir, NullLogger.Instance, protect: false), store,
            new SystemStatusProbeAdapter(_ => new PresenceReport(1, DeviceKind.Pc)), TimeProvider.System,
            NullLogger<HubLinkService>.Instance, allowLoopback: true,
            finder: new HubFinder(_ => Array.Empty<MobileEndpoint>(), NullLogger.Instance));
        var own = new PairingOffer(1, "pc-a", "ME", Array.Empty<string>(), 47820, PairingService.Base64Url(new byte[16]),
            DateTimeOffset.UtcNow.AddMinutes(5), new[] { new MobileEndpoint(MobileTransport.Tailscale, "100.1.2.3", 47820, false) });
        var other = own with { PcId = "pc-z" };

        Assert.Equal("self", (await links.LinkAsync(ConnectionCode.Encode(own)!, default)).Error);
        Assert.Equal("invalid_code", (await links.LinkAsync("abandon ability able about above absent absorb", default)).Error);
        Assert.Equal("not_found", (await links.LinkAsync(ConnectionCode.Encode(other)!, default)).Error);
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
