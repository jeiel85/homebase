using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>LAN (TLS + pin), endpoint list and Cloudflare helpers (DECISIONS M-15~M-17).</summary>
public class MobileNetworkPathTests
{
    private readonly ITestOutputHelper _output;

    public MobileNetworkPathTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.45.101", true)]
    [InlineData("192.169.0.1", false)]
    [InlineData("100.101.102.103", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    public void Private_lan_ranges_are_rfc1918(string ip, bool expected) =>
        Assert.Equal(expected, LanAddressResolver.IsPrivateV4(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("192.168.45.7", true)]
    [InlineData("192.168.46.7", false)]
    [InlineData("10.0.0.1", false)]
    public void Same_subnet_uses_the_local_prefix(string peer, bool expected) =>
        Assert.Equal(expected, LanAddressResolver.InSameSubnet(IPAddress.Parse(peer),
            new[] { new LanAddress(IPAddress.Parse("192.168.45.101"), 24) }));

    [Theory]
    [InlineData("home-pc.example.com", "home-pc.example.com")]
    [InlineData("https://Home-PC.Example.com/", "home-pc.example.com")]
    [InlineData("home-pc.example.com:443", "home-pc.example.com")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Cloudflare_hostname_is_normalized(string? raw, string? expected) =>
        Assert.Equal(expected, CloudflareTunnel.CleanHostname(raw));

    [Fact]
    public void Quick_tunnel_address_is_read_from_the_connector_log()
    {
        const string line = "2026-11-13T10:00:00Z INF |  https://brave-otter-sample-words.trycloudflare.com                    |";
        Assert.Equal("brave-otter-sample-words.trycloudflare.com", CloudflareTunnel.ParseQuickTunnelHost(line));
        Assert.Null(CloudflareTunnel.ParseQuickTunnelHost("INF Requesting new quick Tunnel on trycloudflare.com..."));
    }

    [Fact]
    public void Cloudflared_at_a_missing_configured_path_is_not_found() =>
        Assert.Null(CloudflareTunnel.LocateCloudflared(@"C:\definitely\not\here\cloudflared.exe"));

    [Fact]
    public void Env_indirection_resolves_the_tunnel_token()
    {
        var name = "HB_TEST_TUNNEL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, " tok ");
        try
        {
            Assert.Equal("tok", new CloudflareOptions { Token = "ENV:" + name }.ResolveToken());
            Assert.Null(new CloudflareOptions { Token = "ENV:HB_TEST_MISSING_" + Guid.NewGuid().ToString("N") }.ResolveToken());
            Assert.Null(new CloudflareOptions { Token = "" }.ResolveToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Lan_path_serves_tls_with_the_pinned_certificate_and_lists_it_in_the_offer()
    {
        var lan = new LanAddressResolver(NullLogger<LanAddressResolver>.Instance).FindLanAddresses().FirstOrDefault();
        if (lan is null)
        {
            // Needs a gateway adapter with a private IPv4 address (dev PCs and CI runners have one).
            _output.WriteLine("No private LAN address with a gateway on this machine; LAN TLS round-trip not exercised.");
            return;
        }

        var port = FreePort();
        using var cert = MobileTlsCertificate.Create(persistKey: false);
        var pin = MobileTlsCertificate.Sha256Hex(cert);
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, Lan = true, Port = port });
        var store = new InMemoryDeviceStore();
        var pairing = new PairingService(store, TimeProvider.System);
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var api = new MobileApiServer(hub, pairing, store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new FixedLan(lan), options, NullLogger<MobileApiServer>.Instance, lanCertificate: cert);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await api.StartAsync(cts.Token);
        try
        {
            var offer = await WaitForOfferAsync(api, cts.Token);
            var endpoint = Assert.Single(offer.Endpoints);
            Assert.Equal(MobileTransport.Lan, endpoint.Transport);
            Assert.True(endpoint.Tls);
            Assert.Equal(pin, endpoint.CertSha256);
            Assert.Empty(offer.Hosts); // v1 apps can't do TLS, so LAN stays out of the legacy list

            var url = $"https://{lan.Address}:{port}/api/v1/info";
            using (var pinned = PinnedClient(pin))
            {
                var info = JsonSerializer.Deserialize<PcInfo>(await pinned.GetStringAsync(url, cts.Token), MobileProtocol.Json)!;
                Assert.Equal(store.PcId, info.PcId);
            }

            using (var wrongPin = PinnedClient(new string('0', 64)))
                await Assert.ThrowsAsync<HttpRequestException>(() => wrongPin.GetStringAsync(url, cts.Token));

            using (var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                await Assert.ThrowsAnyAsync<Exception>(() => plain.GetStringAsync($"http://{lan.Address}:{port}/api/v1/info", cts.Token));
        }
        finally
        {
            await api.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Endpoints_route_needs_the_bearer_token()
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var store = new InMemoryDeviceStore();
        var pairing = new PairingService(store, TimeProvider.System);
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var api = new MobileApiServer(hub, pairing, store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance);
        var (code, _) = pairing.Begin();
        var token = pairing.TryPair(code, "Phone", null).Token!;

        var (denied, _) = await api.HandleRestAsync(Req("/api/v1/endpoints", null), null, CancellationToken.None);
        Assert.Equal(401, denied);

        var (ok, json) = await api.HandleRestAsync(Req("/api/v1/endpoints", token), null, CancellationToken.None);
        Assert.Equal(200, ok);
        var body = JsonSerializer.Deserialize<EndpointsResponse>(json, MobileProtocol.Json)!;
        Assert.Equal(MobileProtocol.Schema, body.Schema);
        Assert.NotNull(body.Endpoints);
    }

    private static HttpRequestHead Req(string path, string? token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return new HttpRequestHead("GET", path, headers);
    }

    /// <summary>What the Android app does: accept exactly the certificate whose SHA-256 is pinned.</summary>
    private static HttpClient PinnedClient(string pin) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null && Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(pin, StringComparison.OrdinalIgnoreCase),
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<PairingOffer> WaitForOfferAsync(MobileApiServer api, CancellationToken ct)
    {
        while (true)
        {
            if (api.CreateOffer() is { } offer) return offer;
            await Task.Delay(50, ct);
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

    private sealed class FixedLan : LanAddressResolver
    {
        private readonly LanAddress _address;

        public FixedLan(LanAddress address) : base(NullLogger<LanAddressResolver>.Instance) => _address = address;

        public override IReadOnlyList<LanAddress> FindLanAddresses() => new[] { _address };
    }
}
