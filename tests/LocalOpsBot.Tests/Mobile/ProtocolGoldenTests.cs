using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>
/// Pins the wire format to <c>docs/mobile-protocol/*.json</c>. The Android app's unit tests parse
/// the same files, so a field rename on either side fails a test instead of a phone.
/// Set HOMEBASE_UPDATE_GOLDEN=1 to rewrite the fixtures after an intentional schema change.
/// </summary>
public class ProtocolGoldenTests
{
    public static LiveSnapshot SampleSnapshot() => new(
        MobileProtocol.Schema,
        new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero),
        "HOME-PC",
        new SystemTotals(23.4, 12_884_901_888, 34_359_738_368, 1_048_576, 204_800, 51_200, 93_784),
        SessionAvailable: true,
        NetPerProcess: true,
        new[]
        {
            new AppEntry(15816, "WindowsTerminal", true,
                new[] { new WindowTitle("✳ Homebase 모바일 앱", true) },
                new ResourceUsage(31.5, 734_003_200, 4096, 2048, 1024),
                new[] { new ProcessEntry(20480, 15816, "node", new ResourceUsage(30.0, 600_000_000, 4096, 2048, 1024)) }),
            new AppEntry(33052, "chrome", false,
                new[] { new WindowTitle("Release notes - Google Chrome", false) },
                new ResourceUsage(2.1, 1_200_000_000, 0, null, null),
                Array.Empty<ProcessEntry>()),
        },
        new[] { new ProcessEntry(20480, 15816, "node", new ResourceUsage(30.0, 600_000_000, 4096, 2048, 1024)) },
        new[] { new ClaudeSession("Homebase 모바일 앱", @"D:\Project\homebase", new DateTimeOffset(2026, 11, 13, 9, 29, 0, TimeSpan.Zero)) });

    public static IReadOnlyList<MobileEndpoint> SampleEndpoints() => new[]
    {
        new MobileEndpoint(MobileTransport.Lan, "192.168.45.101", 47820, true,
            "3f6c1b1e8f0d4a7b9c2e5d8a1b4c7e0f3a6d9c2b5e8f1a4d7c0b3e6f9a2d5c8b"),
        new MobileEndpoint(MobileTransport.Tailscale, "home-pc.tail1234.ts.net", 47820, false),
        new MobileEndpoint(MobileTransport.Tailscale, "100.101.102.103", 47820, false),
        new MobileEndpoint(MobileTransport.Cloudflare, "home-pc.example.com", 443, true),
    };

    public static PairingOffer SampleOffer() => new(1, "6f1c0d2e9b8a4c7d", "HOME-PC",
        new[] { "home-pc.tail1234.ts.net", "100.101.102.103" }, 47820, "q8V3k1yZp0Wm2LrT5nXa9Q",
        new DateTimeOffset(2026, 11, 13, 9, 35, 0, TimeSpan.Zero), SampleEndpoints());

    // RFC 8291 Appendix A keys, so the Kotlin test can also check they decode to a P-256 point.
    public static PushSubscriptionRequest SampleSubscription() => new(MobileProtocol.Schema,
        "https://ntfy.sh/upK3x9QeZt0b2Wm7?up=1",
        "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4",
        "BTBZMqHH6r4Tts7J_aSIgg");

    public static PushMessage SamplePushMessage() => new(MobileProtocol.Schema,
        "5b0e1c2d3f4a49b8a1c2d3e4f5a6b7c8", "6f1c0d2e9b8a4c7d", "HOME-PC", "windows-toast", PushPriority.Info,
        "KakaoTalk: 알림 3건", "엄마: 저녁 먹었니?\n팀장: 회의 10분 뒤", "KakaoTalk", 3,
        new DateTimeOffset(2026, 11, 13, 9, 31, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("push-subscription.v1.json")]
    [InlineData("push-subscription-response.v1.json")]
    [InlineData("push-message.v1.json")]
    [InlineData("snapshot.v1.json")]
    [InlineData("pairing-offer.v1.json")]
    [InlineData("endpoints.v1.json")]
    public void Serialized_contract_matches_the_shared_fixture(string file)
    {
        object sample = file switch
        {
            "snapshot.v1.json" => SampleSnapshot(),
            "pairing-offer.v1.json" => SampleOffer(),
            "push-subscription.v1.json" => SampleSubscription(),
            "push-subscription-response.v1.json" => new PushSubscriptionResponse(MobileProtocol.Schema, "a1b2c3d4e5f6", true),
            "push-message.v1.json" => SamplePushMessage(),
            _ => new EndpointsResponse(MobileProtocol.Schema, SampleEndpoints()),
        };
        var actual = JsonSerializer.SerializeToNode(sample, sample.GetType(), MobileProtocol.Json)!;
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", file);

        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions(MobileProtocol.Json)
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n");
        }

        var expected = JsonNode.Parse(File.ReadAllText(path));
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{file} drifted from the serialized contract:\n{actual}");
    }

    /// <summary>Token whose SHA-256 signs <c>push-endpoints.v1.json</c>; the Kotlin test verifies with it.</summary>
    public const string EndpointsFixtureToken = "fixture-token";

    /// <summary>
    /// <c>push-endpoints.v1.json</c> (M-23): the exact plaintext the PC pushes with its paths. The Kotlin
    /// test checks the MAC with <see cref="EndpointsFixtureToken"/> and reads back the endpoints fixture's paths.
    /// </summary>
    [Fact]
    public void Endpoints_push_matches_the_shared_fixture()
    {
        var bytes = PushEndpoints.Serialize("6f1c0d2e9b8a4c7d", SampleEndpoints(),
            new DateTimeOffset(2026, 11, 14, 8, 30, 0, TimeSpan.Zero), PairingService.HashToken(EndpointsFixtureToken));
        var actual = JsonNode.Parse(bytes)!;
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "push-endpoints.v1.json");

        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual), $"push-endpoints.v1.json drifted:\n{actual}");
        var data = JsonSerializer.Deserialize<PushEndpointsData>(Base64Url.Decode((string?)actual["data"]), MobileProtocol.Json)!;
        Assert.Equal(SampleEndpoints(), data.Endpoints);
        Assert.Equal("6f1c0d2e9b8a4c7d", data.PcId);
    }

    /// <summary>The QR text (M-20). The Android app parses the same file back into the offer fixture's paths.</summary>
    [Fact]
    public void Pairing_link_matches_the_shared_fixture_and_round_trips()
    {
        var link = PairingLink.Build(SampleOffer());
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "pairing-link.v1.txt");
        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1") File.WriteAllText(path, link + "\n");

        Assert.Equal(File.ReadAllText(path).Trim(), link);
        Assert.StartsWith("https://jeiel85.github.io/homebase/pair/#", link);
        Assert.DoesNotContain("?", link); // nothing outside the fragment reaches the web server

        var json = JsonNode.Parse(PairingLink.DecodeJson(link)!)!;
        Assert.Equal("q8V3k1yZp0Wm2LrT5nXa9Q", (string?)json["code"]);
        Assert.Null(json["hosts"]);
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(SampleEndpoints(), MobileProtocol.Json), json["endpoints"]));
        Assert.Null(PairingLink.DecodeJson("https://example.com/#abc"));
    }

    /// <summary>
    /// <c>push-message.v1.aes128gcm.txt</c>: the push-message fixture as the PC encrypts it (RFC 8291,
    /// Appendix A server key and salt, so the output is deterministic). The Kotlin test decrypts it with
    /// the Appendix A phone key, proving the two implementations agree end to end.
    /// </summary>
    [Fact]
    public void Encrypted_push_fixture_matches_the_pc_encryption()
    {
        using var asKey = LocalOpsBot.Tests.Push.WebPushEncryptionTests.Key(
            LocalOpsBot.Tests.Push.WebPushEncryptionTests.AsPrivate, LocalOpsBot.Tests.Push.WebPushEncryptionTests.AsPublic);
        var body = WebPushEncryption.Encrypt(
            PushPayload.Serialize(SamplePushMessage()),
            Base64Url.Decode(LocalOpsBot.Tests.Push.WebPushEncryptionTests.UaPublic),
            Base64Url.Decode(LocalOpsBot.Tests.Push.WebPushEncryptionTests.AuthSecret),
            asKey,
            Base64Url.Decode(LocalOpsBot.Tests.Push.WebPushEncryptionTests.Salt));
        var actual = Base64Url.Encode(body);
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "push-message.v1.aes128gcm.txt");

        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual + "\n");

        Assert.Equal(File.ReadAllText(path).Trim(), actual);
    }

    [Fact]
    public void Endpoints_without_a_pin_omit_the_field()
    {
        var json = JsonSerializer.Serialize(new MobileEndpoint(MobileTransport.Tailscale, "100.1.2.3", 47820, false), MobileProtocol.Json);
        Assert.DoesNotContain("certSha256", json);
    }

    [Fact]
    public void Null_network_rates_are_omitted_so_the_app_shows_a_dash()
    {
        var json = JsonSerializer.Serialize(new ResourceUsage(1, 2, 3, null, null), MobileProtocol.Json);
        Assert.DoesNotContain("netRxBps", json);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LocalOpsBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
