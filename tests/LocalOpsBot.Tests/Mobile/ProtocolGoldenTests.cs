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
        new[] { new ClaudeSession("Homebase 모바일 앱", @"D:\Project\homebase", new DateTimeOffset(2026, 11, 13, 9, 29, 0, TimeSpan.Zero)) },
        new[]
        {
            new WorkItem(WorkKind.Agent, "Claude Code", "Homebase 모바일 앱", @"D:\Project\homebase", new DateTimeOffset(2026, 11, 13, 9, 29, 0, TimeSpan.Zero),
                Agent: new AgentInfo(AgentStatus.Waiting, "Bash 실행 승인", new DateTimeOffset(2026, 11, 13, 9, 28, 0, TimeSpan.Zero),
                    Model: "claude-opus-5-5", ContextTokens: 184503, LastTool: "Bash", Mode: "plan", PrNumber: 56,
                    RemoteUrl: "https://claude.ai/code/session_01KuE5FPMg4WPj9yciyPDgKg", SessionId: "a26bf009-27c0-456f-a353-6b48a3d785e2")),
            new WorkItem(WorkKind.Agent, "Codex", "OpenCode에 모델 추가", @"C:\Users\me\Documents\Codex", new DateTimeOffset(2026, 11, 13, 9, 25, 0, TimeSpan.Zero),
                Agent: new AgentInfo(AgentStatus.Busy, StatusSince: new DateTimeOffset(2026, 11, 13, 9, 20, 0, TimeSpan.Zero),
                    Model: "gpt-5.5", Effort: "high", ContextTokens: 42000, ContextWindow: 258400, TotalTokens: 51000,
                    LastTool: "apply_patch", SessionId: "01a1e375-84c9-7741-a7f3-120940bdace8")),
            new WorkItem(WorkKind.Document, "Word", "견적서.docx", Foreground: true),
            new WorkItem(WorkKind.Editor, "VS Code", "Program.cs", "homebase"),
            new WorkItem(WorkKind.Browser, "Chrome", "Release notes"),
        },
        new HardwareInfo(
            "AMD Ryzen 7 250 w/ Radeon 780M Graphics",
            CpuTempC: 61.5,
            BoardTempC: null,
            new[]
            {
                new GpuReading("NVIDIA GeForce RTX 5060 Laptop GPU", "nvidia", false,
                    LoadPct: 37, TempC: 54, TempMaxC: 89,
                    VramUsedBytes: 1_492_123_648, VramTotalBytes: 8_546_942_976,
                    SharedUsedBytes: 338_051_072, SharedTotalBytes: 8_217_880_576,
                    PowerW: 41.2, PowerLimitW: null, PowerPct: null, FanPct: null, FanRpm: null,
                    ClockMhz: 1_905, MemClockMhz: 7_001, Source: "nvidia-smi+windows"),
                new GpuReading("AMD Radeon 780M Graphics", "amd", true,
                    LoadPct: 4, TempC: 53, TempMaxC: null,
                    VramUsedBytes: 379_809_792, VramTotalBytes: 536_870_912,
                    SharedUsedBytes: 355_807_232, SharedTotalBytes: 8_217_880_576,
                    PowerW: null, PowerLimitW: null, PowerPct: 3.2, FanPct: null, FanRpm: null,
                    ClockMhz: null, MemClockMhz: 1_600, Source: "windows"),
            },
            new[] { new DiskUsage("C:", 510_891_388_928, 327_434_858_496), new DiskUsage("D:", 1_447_034_875_904, 1_400_076_177_408) },
            new PingStats("1.1.1.1", LastMs: 8, AvgMs: 10.3, LossPct: 0, Samples: 30)));

    // A phone's report (M-27) with the v0.33.0 display fields (M-44); the Kotlin test builds the same one.
    public static PresenceReport SamplePresence() => new(MobileProtocol.Schema, DeviceKind.Phone, "0.33.0",
        Battery: 76, Charging: true, Network: "wifi", MemUsedBytes: 5_368_709_120, MemTotalBytes: 11_811_160_064, UptimeSec: 259_200,
        Os: "Android 15", StorageUsedBytes: 143_881_404_416, StorageTotalBytes: 255_550_554_112, BatteryTempC: 31.5,
        BatteryHealth: "good", Plug: "usb", ChargeFullSec: 2_100, PowerSave: false, Thermal: "none", Signal: 4, LinkMbps: 866,
        Forwarding: true, Dnd: false);

    public static DevicesResponse SampleDevices() => new(MobileProtocol.Schema,
        new HubEntry("6f1c0d2e9b8a4c7d", "HOME-PC", "0.22.0",
            new DeviceStatus("0.22.0", CpuPct: 23.4, MemUsedBytes: 12_884_901_888, MemTotalBytes: 34_359_738_368, UptimeSec: 93_784,
                Os: "Windows 11 Pro 24H2")),
        new[]
        {
            new DeviceEntry("a1b2c3d4e5f6", "WORK-PC", DeviceKind.Pc, "Windows", new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.Zero), true,
                new DeviceStatus("0.22.0", CpuPct: 8, MemUsedBytes: 8_589_934_592, MemTotalBytes: 17_179_869_184, UptimeSec: 7200,
                    Os: "Windows 11 Home 24H2"), PcId: "a1b2c3d4e5f60718"),
            new DeviceEntry("0f9e8d7c6b5a", "Galaxy S24", DeviceKind.Phone, "SM-S921N", new DateTimeOffset(2026, 11, 14, 9, 29, 0, TimeSpan.Zero), true,
                SamplePresence().ToStatus()),
            new DeviceEntry("1234567890ab", "Galaxy Tab", DeviceKind.Phone, "SM-X710", new DateTimeOffset(2026, 11, 13, 22, 0, 0, TimeSpan.Zero), false, null),
        },
        new[] { "3f6c1b1e8f0d4a7b9c2e5d8a1b4c7e0f3a6d9c2b5e8f1a4d7c0b3e6f9a2d5c8b" });

    // The device group (M-47): HOME-PC holds WORK-PC's token, LAPTOP linked to it only one way so far.
    // The group's aliases (M-51): WORK-PC and a phone named, LAPTOP's alias cleared.
    public static GroupResponse SampleGroup() => new(MobileProtocol.Schema, "6f1c0d2e9b8a4c7d", "HOME-PC",
        new[] { new GroupMember("3b9e1f5a7c2d4e60", "LAPTOP", false), new GroupMember("a1b2c3d4e5f60718", "WORK-PC", true) },
        new[] { new GroupRemoval("pc:0fedcba987654321", new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero)) },
        new[]
        {
            new DeviceAlias("pc:a1b2c3d4e5f60718", "WORK-PC", "회사", new DateTimeOffset(2026, 11, 17, 8, 30, 0, 125, TimeSpan.Zero)),
            new DeviceAlias("phone-name:galaxy s24|sm-s921n", "Galaxy S24", "내 폰", new DateTimeOffset(2026, 11, 17, 8, 31, 0, TimeSpan.Zero)),
            new DeviceAlias("pc:3b9e1f5a7c2d4e60", "LAPTOP", null, new DateTimeOffset(2026, 11, 17, 8, 32, 0, TimeSpan.Zero)),
        });

    public static DeviceNotification SampleDeviceNotification() => new(MobileProtocol.Schema,
        "9c1d2e3f4a5b46c7d8e9f0a1b2c3d4e5", "카카오톡", "엄마", "저녁 먹었니?", new DateTimeOffset(2026, 11, 14, 9, 31, 0, TimeSpan.Zero));

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
        "BTBZMqHH6r4Tts7J_aSIgg",
        ["advisor", "event_log"],
        [ClipProtocol.Feature]);

    /// <summary>A Claude Code permission prompt (M-48): the phone shows Allow / Deny for <c>approval.id</c>.</summary>
    public static PushMessage SampleApprovalPush() => new(MobileProtocol.Schema,
        "9c1d2e3f4a5b46c7d8e9f0a1b2c3d4e5", "6f1c0d2e9b8a4c7d", "HOME-PC", "agent-approval", PushPriority.Critical,
        "명령 실행", "알림 개선\nPush the release\n\ngit push origin main", "Claude Code", 1,
        new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero),
        new PushApproval("0f1e2d3c4b5a69788796a5b4c3d2e1f0", new DateTimeOffset(2026, 11, 17, 1, 10, 0, TimeSpan.Zero)));

    /// <summary><c>POST /api/v1/advise</c> (M-60): the app shows <c>text</c> as plain text and <c>summary</c> as its basis.</summary>
    public static AdviceResponse SampleAdvice() => new(MobileProtocol.Schema, true,
        "- CPU가 92%로 오래 바쁩니다. 작업 관리자에서 chrome을 확인하세요.\n- C: 드라이브 여유가 5%뿐입니다. 임시 파일을 정리하세요.",
        "Host: HOME-PC (Windows 11)\nCPU load: 92%\nRAM: 12.0/32.0 GB (38%)\nDisk C:\\: 24 GB free / 476 GB (95% used)",
        "llama3.2:1b", new DateTimeOffset(2026, 11, 19, 1, 30, 0, TimeSpan.Zero), Cached: false);

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
    [InlineData("presence.v1.json")]
    [InlineData("devices.v1.json")]
    [InlineData("device-notification.v1.json")]
    [InlineData("group.v1.json")]
    [InlineData("push-approval.v1.json")]
    [InlineData("advice.v1.json")]
    public void Serialized_contract_matches_the_shared_fixture(string file)
    {
        object sample = file switch
        {
            "snapshot.v1.json" => SampleSnapshot(),
            "pairing-offer.v1.json" => SampleOffer(),
            "push-subscription.v1.json" => SampleSubscription(),
            "push-subscription-response.v1.json" => new PushSubscriptionResponse(MobileProtocol.Schema, "a1b2c3d4e5f6", true),
            "push-message.v1.json" => SamplePushMessage(),
            "presence.v1.json" => SamplePresence(),
            "devices.v1.json" => SampleDevices(),
            "device-notification.v1.json" => SampleDeviceNotification(),
            "group.v1.json" => SampleGroup(),
            "push-approval.v1.json" => SampleApprovalPush(),
            "advice.v1.json" => SampleAdvice(),
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

    /// <summary>Fixed nonces so the clipboard fixtures (M-62) are deterministic; real messages use random ones.</summary>
    public static readonly byte[] ClipFixtureNonce = Enumerable.Range(0, ClipCrypto.NonceBytes).Select(i => (byte)i).ToArray();
    public static readonly byte[] ClipPushFixtureNonce = Enumerable.Range(0x20, ClipCrypto.NonceBytes).Select(i => (byte)i).ToArray();

    public const string ClipFixtureText = "회의 링크: https://meet.example.com/abc-defg-hij\n비밀번호는 없습니다 \"그대로\" 붙여 넣으세요";
    public const string ClipPushFixtureText = "택배 송장번호 6081-2345-6789";

    /// <summary>
    /// The clipboard key of the fixtures (M-62): what a device holding the RFC 8291 Appendix A user-agent key and a PC
    /// holding its application-server key agree. The Kotlin test derives it from the phone side.
    /// </summary>
    public static byte[] ClipFixtureKey()
    {
        using var pcKey = LocalOpsBot.Tests.Push.WebPushEncryptionTests.Key(
            LocalOpsBot.Tests.Push.WebPushEncryptionTests.AsPrivate, LocalOpsBot.Tests.Push.WebPushEncryptionTests.AsPublic);
        return ClipKeys.Answer(new ClipKeyExchange(MobileProtocol.Schema, LocalOpsBot.Tests.Push.WebPushEncryptionTests.UaPublic), pcKey)!.Value.Key;
    }

    /// <summary>
    /// <c>clip-key.v1.json</c> (M-62): the two public keys of a <c>POST /api/v1/clip/key</c> exchange and the key they
    /// agree. The Kotlin test computes the same key from the device's private key and the PC's public key.
    /// </summary>
    [Fact]
    public void Clip_key_agreement_matches_the_shared_fixture()
    {
        using var pcKey = LocalOpsBot.Tests.Push.WebPushEncryptionTests.Key(
            LocalOpsBot.Tests.Push.WebPushEncryptionTests.AsPrivate, LocalOpsBot.Tests.Push.WebPushEncryptionTests.AsPublic);
        var (answer, key) = ClipKeys.Answer(new ClipKeyExchange(MobileProtocol.Schema, LocalOpsBot.Tests.Push.WebPushEncryptionTests.UaPublic), pcKey)!.Value;
        Assert.Equal(LocalOpsBot.Tests.Push.WebPushEncryptionTests.AsPublic, answer.Pub);

        var actual = new JsonObject
        {
            ["schema"] = MobileProtocol.Schema,
            ["devicePublic"] = LocalOpsBot.Tests.Push.WebPushEncryptionTests.UaPublic,
            ["pcPublic"] = answer.Pub,
            ["key"] = Base64Url.Encode(key),
        };
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "clip-key.v1.json");
        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual), $"clip-key.v1.json drifted:\n{actual}");
        // The device's side agrees on the same key.
        using var deviceKey = LocalOpsBot.Tests.Push.WebPushEncryptionTests.Key(
            LocalOpsBot.Tests.Push.WebPushEncryptionTests.UaPrivate, LocalOpsBot.Tests.Push.WebPushEncryptionTests.UaPublic);
        Assert.Equal(key, ClipKeys.Finish(deviceKey, answer));
    }

    /// <summary>
    /// <c>clip.v1.json</c> (M-62): a phone's <c>POST /api/v1/clip</c>, sealed with <see cref="ClipFixtureKey"/>.
    /// The Kotlin test opens it with the key it derives and gets <see cref="ClipFixtureText"/> back.
    /// </summary>
    [Fact]
    public void Clip_envelope_matches_the_shared_fixture()
    {
        var key = ClipFixtureKey();
        var content = new ClipContent(MobileProtocol.Schema, ClipFixtureText, ClipFixtureText.Length,
            new DateTimeOffset(2026, 11, 19, 4, 0, 0, TimeSpan.Zero));
        var envelope = ClipMessages.Seal(key, "7d3c9e1f2a4b4c5d8e6f0a1b2c3d4e5f", content, ClipFixtureNonce);
        var actual = JsonSerializer.SerializeToNode(envelope, MobileProtocol.Json)!;
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "clip.v1.json");

        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual), $"clip.v1.json drifted:\n{actual}");
        var opened = ClipMessages.Open(key, JsonSerializer.Deserialize<ClipEnvelope>(File.ReadAllText(path), MobileProtocol.Json));
        Assert.Equal(content, opened);
    }

    /// <summary>
    /// <c>push-clip.v1.json</c> (M-62): the plaintext push carrying clipboard text to a phone, sealed with
    /// <see cref="ClipFixtureKey"/>. The Kotlin test opens it and reads <see cref="ClipPushFixtureText"/>.
    /// </summary>
    [Fact]
    public void Clip_push_matches_the_shared_fixture()
    {
        var key = ClipFixtureKey();
        var content = new ClipContent(MobileProtocol.Schema, ClipPushFixtureText, ClipPushFixtureText.Length,
            new DateTimeOffset(2026, 11, 19, 4, 5, 0, TimeSpan.Zero), Sensitive: true);
        var bytes = ClipMessages.PushPlaintext("6f1c0d2e9b8a4c7d", key, "1a2b3c4d5e6f40718293a4b5c6d7e8f9", content, ClipPushFixtureNonce)!;
        var actual = JsonNode.Parse(bytes)!;
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "push-clip.v1.json");

        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual), $"push-clip.v1.json drifted:\n{actual}");
        Assert.Null(actual["title"]); // an app from before M-62 drops it instead of showing an empty notification
        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(bytes, MobileProtocol.Json)!;
        var opened = ClipMessages.Open(key, new ClipEnvelope(push.Schema, push.Id, push.Nonce, push.Data));
        Assert.Equal(content, opened);
    }

    public const string TransferFixtureId = "0c1d2e3f405162738495a6b7c8d9eafb";
    public static readonly byte[] TransferFixtureFile0 = System.Text.Encoding.UTF8.GetBytes("안건: 클립보드 2단계\n결정: 휴대폰 → PC 먼저\n");
    public static readonly byte[] TransferFixtureFile1 = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// <c>transfer.v1.json</c> (M-64): a device's transfer of two small files to a PC — the sealed metadata, one chunk of
    /// each file and the sealed hashes, all with <see cref="ClipFixtureKey"/> and fixed nonces. The Kotlin test derives
    /// the transfer key, opens each piece and checks the plaintexts and hashes it lists.
    /// </summary>
    [Fact]
    public void Transfer_matches_the_shared_fixture()
    {
        var key = ClipFixtureKey();
        var meta = new TransferMeta(MobileProtocol.Schema,
            new[] { new TransferFile("회의록 11월.txt", TransferFixtureFile0.Length, "text/plain"), new TransferFile("screen.png", TransferFixtureFile1.Length, "image/png") },
            new DateTimeOffset(2026, 11, 19, 6, 0, 0, TimeSpan.Zero));
        var hashes = new TransferHashes(MobileProtocol.Schema, new[]
        {
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TransferFixtureFile0)).ToLowerInvariant(),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TransferFixtureFile1)).ToLowerInvariant(),
        });
        var transferKey = TransferCrypto.TransferKey(key, TransferFixtureId);
        var metaNonce = Enumerable.Range(0x40, ClipCrypto.NonceBytes).Select(i => (byte)i).ToArray();
        var hashNonce = Enumerable.Range(0x50, ClipCrypto.NonceBytes).Select(i => (byte)i).ToArray();

        var actual = new JsonObject
        {
            ["schema"] = MobileProtocol.Schema,
            ["transferId"] = TransferFixtureId,
            ["meta"] = JsonSerializer.SerializeToNode(TransferMessages.SealMeta(key, TransferFixtureId, meta, metaNonce), MobileProtocol.Json),
            ["chunks"] = new JsonArray(
                new JsonObject { ["f"] = 0, ["n"] = 0, ["plain"] = Base64Url.Encode(TransferFixtureFile0),
                    ["data"] = Base64Url.Encode(TransferCrypto.SealChunk(transferKey, 0, 0, TransferFixtureFile0)) },
                new JsonObject { ["f"] = 1, ["n"] = 0, ["plain"] = Base64Url.Encode(TransferFixtureFile1),
                    ["data"] = Base64Url.Encode(TransferCrypto.SealChunk(transferKey, 1, 0, TransferFixtureFile1)) }),
            ["hashes"] = JsonSerializer.SerializeToNode(TransferMessages.SealHashes(key, TransferFixtureId, hashes, hashNonce), MobileProtocol.Json),
        };
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "transfer.v1.json");
        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual), $"transfer.v1.json drifted:\n{actual}");
        var fixture = JsonNode.Parse(File.ReadAllText(path))!;
        var openedMeta = TransferMessages.OpenMeta(key, fixture["meta"].Deserialize<ClipEnvelope>(MobileProtocol.Json))!;
        Assert.Equal(meta.Files, openedMeta.Files);
        Assert.Equal(hashes.Sha256, TransferMessages.OpenHashes(key, fixture["hashes"].Deserialize<ClipEnvelope>(MobileProtocol.Json))!.Sha256);
        Assert.Equal(TransferFixtureFile1, TransferCrypto.OpenChunk(transferKey, 1, 0, Base64Url.Decode((string?)fixture["chunks"]![1]!["data"])));
        // A chunk at another position doesn't open.
        Assert.Null(TransferCrypto.OpenChunk(transferKey, 0, 0, Base64Url.Decode((string?)fixture["chunks"]![1]!["data"])));
    }

    /// <summary>
    /// <c>push-share.v1.json</c> (M-65): the plaintext push telling a phone files wait on a PC, sealed with
    /// <see cref="ClipFixtureKey"/>; its id is <see cref="TransferFixtureId"/>. The Kotlin test opens the notice.
    /// </summary>
    [Fact]
    public void Share_push_matches_the_shared_fixture()
    {
        var key = ClipFixtureKey();
        var notice = new ShareNotice(MobileProtocol.Schema, 2, TransferFixtureFile0.Length + TransferFixtureFile1.Length, "회의록 11월.txt",
            new DateTimeOffset(2026, 11, 19, 7, 0, 0, TimeSpan.Zero));
        var bytes = FileSendMessages.PushPlaintext("6f1c0d2e9b8a4c7d", key, TransferFixtureId, notice,
            Enumerable.Range(0x60, ClipCrypto.NonceBytes).Select(i => (byte)i).ToArray());
        var actual = JsonNode.Parse(bytes)!;
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "push-share.v1.json");
        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual), $"push-share.v1.json drifted:\n{actual}");
        Assert.Null(actual["title"]); // an app from before M-65 drops it
        Assert.Equal(notice, FileSendMessages.OpenNotice(key, JsonSerializer.Deserialize<ClipPushEnvelope>(bytes, MobileProtocol.Json)));
    }

    /// <summary>
    /// <c>transfer-offer.v1.json</c> (M-65): <c>GET /api/v1/transfer/offer</c> for the two fixture files, sealed with
    /// <see cref="ClipFixtureKey"/>. The chunks a phone fetches are sealed like <c>transfer.v1.json</c>'s.
    /// </summary>
    [Fact]
    public void Transfer_offer_matches_the_shared_fixture()
    {
        var key = ClipFixtureKey();
        var offer = new TransferOffer(MobileProtocol.Schema, new[]
        {
            new TransferOfferFile("회의록 11월.txt", TransferFixtureFile0.Length, "text/plain",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TransferFixtureFile0)).ToLowerInvariant()),
            new TransferOfferFile("screen.png", TransferFixtureFile1.Length, "image/png",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TransferFixtureFile1)).ToLowerInvariant()),
        }, new DateTimeOffset(2026, 11, 19, 7, 0, 0, TimeSpan.Zero));
        var envelope = FileSendMessages.SealOffer(key, TransferFixtureId, offer, Enumerable.Range(0x70, ClipCrypto.NonceBytes).Select(i => (byte)i).ToArray());
        var actual = JsonSerializer.SerializeToNode(envelope, MobileProtocol.Json)!;
        var path = Path.Combine(RepoRoot(), "docs", "mobile-protocol", "transfer-offer.v1.json");
        if (Environment.GetEnvironmentVariable("HOMEBASE_UPDATE_GOLDEN") == "1")
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual), $"transfer-offer.v1.json drifted:\n{actual}");
        var opened = FileSendMessages.OpenOffer(key, JsonSerializer.Deserialize<ClipEnvelope>(File.ReadAllText(path), MobileProtocol.Json))!;
        Assert.Equal(offer.Files, opened.Files);
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
