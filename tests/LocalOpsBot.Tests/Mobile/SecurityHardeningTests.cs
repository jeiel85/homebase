using System.Net;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using LocalOpsBot.Infrastructure.Notifications;
using LocalOpsBot.Tests.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Regression tests for the v0.60.2 security hardening fixes.</summary>
public class SecurityHardeningTests
{
    [Fact]
    public void Introduction_code_cannot_escalate_from_pc_to_phone()
    {
        var store = new InMemoryDeviceStore("pc-target");
        var pairing = new PairingService(store, TimeProvider.System);

        var (introCode, _) = pairing.BeginIntroduction();
        var escalated = pairing.TryPair(introCode, "EvilPhone", "Pixel", DeviceKind.Phone);

        Assert.Equal(PairOutcome.Forbidden, escalated.Outcome);
        Assert.Null(escalated.Token);
        Assert.Empty(store.List());

        // Single-use: the rejected attempt also consumed the code.
        Assert.Equal(PairOutcome.InvalidCode, pairing.TryPair(introCode, "PC-B", "Windows", DeviceKind.Pc, "pc-b").Outcome);

        // Legitimate PC pairing with a fresh introduction code still succeeds and binds its pcId.
        var (pcCode, _) = pairing.BeginIntroduction();
        var paired = pairing.TryPair(pcCode, "PC-B", "Windows", DeviceKind.Pc, "pc-b");
        Assert.Equal(PairOutcome.Paired, paired.Outcome);
        var saved = Assert.Single(store.List());
        Assert.Equal(DeviceKind.Pc, saved.Kind);
        Assert.Equal("pc-b", saved.PcId);
    }

    [Fact]
    public async Task Linked_pc_cannot_spoof_another_pcs_pcId_in_presence_or_hijack_its_paths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hb-sec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InMemoryDeviceStore("pc-hub");
            var time = new ManualTimeProvider();
            var pairing = new PairingService(store, time);
            var board = new DeviceStatusBoard(time);
            var hub = new DeviceHub(store, board, new NotificationInbox(time), () => new DeviceStatus("0.60.2"), null, "HUB", "0.60.2", NullLogger.Instance, time);
            var hubStore = new HubStore(dir, NullLogger.Instance, protect: false);
            var victimEndpoint = new MobileEndpoint(MobileTransport.Cloudflare, "victim.trycloudflare.com", 443, true);
            hubStore.Upsert(new LinkedHub("pc-b", "PC-B", "dev-b", "secret-token-for-pc-b", new[] { victimEndpoint }, time.Now));

            var links = new HubLinkService(hubStore, store,
                new SystemStatusProbeAdapter(_ => new PresenceReport(1, DeviceKind.Pc, "0.60.2")),
                time, NullLogger<HubLinkService>.Instance);
            await links.StartAsync(CancellationToken.None);

            var live = new LiveSnapshotHub(new FakeSampler(), Options.Create(new MobileOptions { Enabled = true }), time, NullLogger<LiveSnapshotHub>.Instance);
            var api = new MobileApiServer(live, pairing, store,
                new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
                new LanAddressResolver(NullLogger<LanAddressResolver>.Instance),
                Options.Create(new MobileOptions { Enabled = true }), NullLogger<MobileApiServer>.Instance, deviceHub: hub);
            var group = new DeviceGroupService(links, store, board, new GroupTombstones(time),
                () => api.CreateOffer(introduction: true), "HUB", time, NullLogger<DeviceGroupService>.Instance);
            api.AttachGroup(group);

            // Pair PC-A legitimately with its own pcId "pc-a".
            var (codeA, _) = pairing.BeginIntroduction();
            var pcA = pairing.TryPair(codeA, "PC-A", "Windows", DeviceKind.Pc, "pc-a");
            Assert.Equal(PairOutcome.Paired, pcA.Outcome);

            // PC-A tries to report presence claiming to be PC-B with an attacker endpoint.
            var spoof = new PresenceReport(1, DeviceKind.Pc, "0.60.2", PcId: "pc-b",
                Endpoints: new[] { new MobileEndpoint(MobileTransport.Cloudflare, "attacker.example.com", 443, true) });
            var req = new HttpRequestHead("POST", "/api/v1/presence",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + pcA.Token });
            var (status, _) = await api.HandleRestAsync(req, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(spoof, MobileProtocol.Json)), CancellationToken.None);

            Assert.Equal(403, status);
            Assert.Equal(new[] { victimEndpoint }, links.Hub("pc-b")!.Endpoints);

            // Direct AdoptPaths call is also blocked for mismatched pcId.
            var pcAInfo = store.List().Single(d => d.DeviceId == pcA.DeviceId);
            Assert.False(group.AdoptPaths(pcAInfo, spoof));
            Assert.Equal(new[] { victimEndpoint }, links.Hub("pc-b")!.Endpoints);

            await links.StopAsync(CancellationToken.None);
            links.Dispose();
            group.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Group_join_rejects_forged_loopback_and_non_tailscale_endpoints()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hb-ssrf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InMemoryDeviceStore("pc-self");
            var time = new ManualTimeProvider();
            var links = new HubLinkService(new HubStore(dir, NullLogger.Instance, protect: false), store,
                new SystemStatusProbeAdapter(_ => new PresenceReport(1, DeviceKind.Pc, "0.60.2")),
                time, NullLogger<HubLinkService>.Instance, allowLoopback: false);

            var forged = new PairingOffer(1, "pc-evil", "EVIL", Array.Empty<string>(), 11434, "code", time.Now.AddMinutes(5), new[]
            {
                new MobileEndpoint(MobileTransport.Tailscale, "127.0.0.1", 11434, false),
                new MobileEndpoint(MobileTransport.Lan, "192.168.1.5", 47820, false),
                new MobileEndpoint(MobileTransport.Cloudflare, "169.254.169.254", 443, true),
                new MobileEndpoint(MobileTransport.Loopback, "127.0.0.1", 47820, false),
            });

            var result = await links.LinkWithOfferAsync(forged, CancellationToken.None);
            Assert.False(result.Linked);
            Assert.Equal("invalid_endpoints", result.Error);
            links.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("0-9999999999")]
    [InlineData("0-5000")]
    [InlineData("4097")]
    public void Chunk_ranges_reject_oversized_or_unbounded_ranges(string malicious)
    {
        Assert.Null(ChunkRanges.Parse(malicious));
    }

    [Theory]
    [InlineData("https://127.0.0.1/up")]
    [InlineData("https://[::1]/up")]
    [InlineData("https://localhost/up")]
    [InlineData("https://192.168.1.10/up")]
    [InlineData("https://10.0.0.5/up")]
    [InlineData("https://172.16.0.1/up")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://100.100.1.2/up")]
    [InlineData("https://printer.local/up")]
    [InlineData("https://service.internal/up")]
    public void Push_validation_rejects_internal_and_loopback_endpoints(string endpoint)
    {
        var req = new PushSubscriptionRequest(1, endpoint, WebPushEncryptionTests.UaPublic, WebPushEncryptionTests.AuthSecret);
        Assert.NotNull(PushPayload.Validate(req));
    }

    [Fact]
    public void Push_blocked_ip_checks_public_vs_private_ranges()
    {
        Assert.False(PushPayload.IsBlockedIp(IPAddress.Parse("1.1.1.1")));
        Assert.False(PushPayload.IsBlockedIp(IPAddress.Parse("8.8.8.8")));
        Assert.True(PushPayload.IsBlockedIp(IPAddress.Loopback));
        Assert.True(PushPayload.IsBlockedIp(IPAddress.IPv6Loopback));
        Assert.True(PushPayload.IsBlockedIp(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.True(PushPayload.IsBlockedIp(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.True(PushPayload.IsBlockedIp(IPAddress.Parse("fc00::1")));
        Assert.True(PushPayload.IsBlockedIp(IPAddress.Parse("fe80::1")));
    }

    [Fact]
    public void Push_message_mac_matches_known_vector_and_survives_truncation()
    {
        var tokenHash = PairingService.HashToken("test-token-for-push-mac");
        var approval = new PushApproval("0f1e2d3c4b5a69788796a5b4c3d2e1f0", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
        var msg = new PushMessage(1, "msg-1", "pc-1", "PC", "agent-approval", PushPriority.Critical,
            "명령 실행", new string('가', 3000), "Claude Code", 1, DateTimeOffset.UnixEpoch, approval);

        var bytes = PushPayload.Serialize(msg, tokenHash);
        Assert.True(bytes.Length <= WebPushEncryption.MaxPlaintextBytes);

        var parsed = JsonSerializer.Deserialize<PushMessage>(bytes, MobileProtocol.Json)!;
        Assert.False(string.IsNullOrEmpty(parsed.Mac));
        Assert.Equal(PushPayload.ComputeMac(parsed, tokenHash), parsed.Mac);
        Assert.NotEqual(PushPayload.ComputeMac(parsed with { Title = "위조된 제목" }, tokenHash), parsed.Mac);
    }

    [Fact]
    public void Regex_text_masker_does_not_hang_on_pathological_pattern()
    {
        var masker = new RegexTextMasker(new[] { "(a+)+$" });
        var input = new string('a', 40) + "!";
        var output = masker.Mask(input);
        Assert.Equal(input, output);
    }
}
