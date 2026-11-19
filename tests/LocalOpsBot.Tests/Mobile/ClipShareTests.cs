using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>The sealed text itself (DECISIONS M-62): only the device's key opens it, untouched.</summary>
public class ClipCryptoTests
{
    private static readonly string HashA = PairingService.HashToken("token-a");
    private static readonly string HashB = PairingService.HashToken("token-b");

    private static ClipContent Content(string text) =>
        new(MobileProtocol.Schema, text, text.Length, new DateTimeOffset(2026, 11, 19, 4, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Sealed_text_opens_with_the_same_device_key_only()
    {
        var envelope = ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content("안녕하세요 😀"));

        Assert.Equal("안녕하세요 😀", ClipMessages.Open(HashA, envelope)?.Text);
        Assert.Null(ClipMessages.Open(HashB, envelope));
    }

    [Fact]
    public void What_the_phone_seals_opens_here()
    {
        // The app writes ts with Instant.toString() (UTC "Z", milliseconds when there are any), puts "sensitive"
        // only when true, and keeps non-ASCII as UTF-8 (org.json).
        var plain = System.Text.Encoding.UTF8.GetBytes("{\"schema\":1,\"text\":\"휴대폰에서 \\\"복사\\\"\\n둘째 줄\",\"chars\":15,\"ts\":\"2026-11-19T04:00:00.123Z\"}");
        var (nonce, data) = ClipCrypto.Seal(ClipCrypto.Key(HashA), plain);

        var content = ClipMessages.Open(HashA, new ClipEnvelope(1, ClipProtocol.NewId(), nonce, data));

        Assert.NotNull(content);
        Assert.Equal("휴대폰에서 \"복사\"\n둘째 줄", content!.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 4, 0, 0, 123, TimeSpan.Zero), content.Ts);
        Assert.Null(content.Sensitive);
    }

    [Fact]
    public void Every_message_gets_its_own_nonce()
    {
        var one = ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content("same"));
        var two = ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content("same"));
        Assert.NotEqual(one.Nonce, two.Nonce);
        Assert.NotEqual(one.Data, two.Data);
    }

    [Fact]
    public void Altered_or_malformed_messages_do_not_open()
    {
        var envelope = ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content("hello"));
        var data = Base64Url.Decode(envelope.Data);
        data[0] ^= 1;

        Assert.Null(ClipMessages.Open(HashA, envelope with { Data = Base64Url.Encode(data) }));
        Assert.Null(ClipMessages.Open(HashA, envelope with { Nonce = "AAAA" }));
        Assert.Null(ClipMessages.Open(HashA, envelope with { Data = "!!" }));
        Assert.Null(ClipMessages.Open(HashA, envelope with { Id = "not-an-id" }));
        Assert.Null(ClipMessages.Open(HashA, envelope with { Schema = 2 }));
        Assert.Null(ClipMessages.Open(HashA, null));
    }

    [Fact]
    public void Empty_or_too_long_text_is_refused_when_opened()
    {
        Assert.Null(ClipMessages.Open(HashA, ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content(""))));
        var tooLong = new string('x', ClipProtocol.MaxChars + 1);
        Assert.Null(ClipMessages.Open(HashA, ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content(tooLong))));
        var longest = new string('가', ClipProtocol.MaxChars);
        Assert.Equal(longest, ClipMessages.Open(HashA, ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content(longest)))?.Text);
    }

    [Fact]
    public void The_longest_text_still_fits_the_request_body_cap()
    {
        // Worst case for the JSON escaping inside: control characters cost 6 bytes each.
        var envelope = ClipMessages.Seal(HashA, ClipProtocol.NewId(), Content(new string('\u0001', ClipProtocol.MaxChars)));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(envelope, MobileProtocol.Json).Length <= ClipProtocol.MaxBodyBytes);
    }

    [Fact]
    public void A_push_carries_about_a_thousand_korean_characters_and_says_when_it_cannot()
    {
        var fits = new string('가', 900);
        var push = ClipMessages.PushPlaintext("6f1c0d2e9b8a4c7d", HashA, ClipProtocol.NewId(), Content(fits));
        Assert.NotNull(push);
        Assert.True(push!.Length <= WebPushEncryption.MaxPlaintextBytes);

        var tooLong = Content(new string('가', 2000));
        Assert.Null(ClipMessages.PushPlaintext("6f1c0d2e9b8a4c7d", HashA, ClipProtocol.NewId(), tooLong));
        Assert.NotNull(ClipMessages.PushPlaintext("6f1c0d2e9b8a4c7d", HashA, ClipProtocol.NewId(), tooLong with { Text = null }));
    }
}

/// <summary><c>POST</c>/<c>GET /api/v1/clip</c> and the Tray's sends (DECISIONS M-62).</summary>
public class ClipShareApiTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new("6f1c0d2e9b8a4c7d");
    private readonly PairingService _pairing;
    private readonly List<TrayClip> _tray = new();
    private readonly List<(PushTarget Target, byte[] Plaintext)> _pushed = new();
    private readonly ClipShareService _clips;

    public ClipShareApiTests()
    {
        _pairing = new PairingService(_store, _time);
        _clips = new ClipShareService(_store, (t, p, _) =>
        {
            lock (_pushed) _pushed.Add((t, p));
            return Task.FromResult((true, (string?)null));
        }, hubs: null, _time, NullLogger.Instance)
        {
            Tray = c => { _tray.Add(c); return true; },
        };
    }

    private MobileApiServer Api()
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        return new MobileApiServer(hub, _pairing, _store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options,
            NullLogger<MobileApiServer>.Instance, clips: _clips);
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

    private ClipEnvelope Sealed(string token, string text, bool sensitive = false, string? id = null) =>
        ClipMessages.Seal(PairingService.HashToken(token), id ?? ClipProtocol.NewId(),
            new ClipContent(MobileProtocol.Schema, text, text.Length, _time.GetUtcNow(), sensitive ? true : null));

    private static byte[] Body<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, MobileProtocol.Json);

    private PushTarget Phone(string token, string name, bool clip = true)
    {
        var device = _store.Add(name, "SM-S921N", PairingService.HashToken(token));
        _store.SetPushSubscription(device.DeviceId, new PushSubscription("https://ntfy.sh/up" + device.DeviceId, "k", "a",
            _time.GetUtcNow(), Features: clip ? [ClipProtocol.Feature] : null));
        return _store.PushTargets().Single(t => t.DeviceId == device.DeviceId);
    }

    [Fact]
    public async Task Text_from_a_phone_goes_to_the_tray_with_its_sender()
    {
        var token = Pair("Galaxy S24");

        var (status, json) = await Api().HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(token, "택배 6081-2345", sensitive: true)), default);

        Assert.Equal(200, status);
        Assert.Contains("\"accepted\":true", json);
        Assert.Equal(new TrayClip("택배 6081-2345", "Galaxy S24", DeviceKind.Phone, true), Assert.Single(_tray));
    }

    [Fact]
    public async Task The_same_message_again_is_pasted_once()
    {
        var token = Pair("Galaxy");
        var envelope = Sealed(token, "once");
        var api = Api();

        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Single(_tray);

        // Remembered for half an hour, then a new message under that id would be taken again.
        _time.Advance(ClipShareService.SeenFor + TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Equal(2, _tray.Count);
    }

    [Fact]
    public async Task Text_sealed_for_another_device_or_unpaired_is_refused()
    {
        var token = Pair("Galaxy");
        var api = Api();

        Assert.Equal(401, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, "nope"), Body(Sealed("nope", "x")), default)).Status);
        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed("other-token", "x")), default);
        Assert.Equal(400, status);
        Assert.Contains(ClipErrors.Invalid, json);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Encoding.UTF8.GetBytes("{}"), default)).Status);
        Assert.Empty(_tray);
    }

    [Fact]
    public async Task Without_a_tray_the_sender_hears_nobody_is_signed_in_and_can_retry()
    {
        var token = Pair("Galaxy");
        var envelope = Sealed(token, "later");
        _clips.Tray = null;

        var (status, json) = await Api().HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default);
        Assert.Equal(409, status);
        Assert.Contains(ClipErrors.NoUserSession, json);

        // Not remembered as taken: the same message is pasted once a tray is back.
        _clips.Tray = c => { _tray.Add(c); return true; };
        Assert.Equal(200, (await Api().HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Single(_tray);
    }

    [Fact]
    public async Task An_agent_without_clipboard_sharing_answers_404()
    {
        var token = Pair("Galaxy");
        var options = Options.Create(new MobileOptions { Enabled = true });
        var api = new MobileApiServer(new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            _pairing, _store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance);
        Assert.Equal(404, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(token, "x")), default)).Status);
    }

    [Fact]
    public void Only_phones_that_read_clipboard_pushes_are_offered()
    {
        Phone("t1", "Galaxy S24");
        Phone("t2", "Old phone", clip: false);
        Pair("WORK-PC", DeviceKind.Pc); // paired here, but this PC holds no token for it

        var target = Assert.Single(_clips.Targets());
        Assert.Equal(("Galaxy S24", DeviceKind.Phone), (target.Name, target.Kind));
        Assert.StartsWith("device:", target.Key);
    }

    [Fact]
    public async Task Short_text_rides_in_the_push_sealed_for_that_phone()
    {
        var phone = Phone("phone-token", "Galaxy");

        var result = await _clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(phone.DeviceId)], "회의실 B, 3시", true), default);

        var outcome = Assert.Single(result.Results);
        Assert.True(outcome.Ok);
        Assert.Equal("Galaxy", outcome.Name);
        var (target, plaintext) = Assert.Single(_pushed);
        Assert.Equal(phone.DeviceId, target.DeviceId);
        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(plaintext, MobileProtocol.Json)!;
        Assert.Equal((ClipProtocol.PushKind, "6f1c0d2e9b8a4c7d"), (push.Kind, push.PcId));
        var content = ClipMessages.Open(PairingService.HashToken("phone-token"), new ClipEnvelope(1, push.Id, push.Nonce, push.Data))!;
        Assert.Equal(("회의실 B, 3시", true), (content.Text, content.Sensitive));
        Assert.Null(ClipMessages.Open(PairingService.HashToken("other"), new ClipEnvelope(1, push.Id, push.Nonce, push.Data)));
    }

    [Fact]
    public async Task Long_text_is_fetched_by_the_phone_it_was_sent_to_within_ten_minutes()
    {
        var phone = Phone("phone-token", "Galaxy");
        var otherToken = Pair("Other phone");
        var text = string.Concat(Enumerable.Repeat("긴 로그 한 줄입니다.\n", 400));

        await _clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(phone.DeviceId)], text), default);

        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(Assert.Single(_pushed).Plaintext, MobileProtocol.Json)!;
        var notice = ClipMessages.Open(PairingService.HashToken("phone-token"), new ClipEnvelope(1, push.Id, push.Nonce, push.Data));
        // Opened as a notice: no text, only its length (Open refuses content without text, so read it raw).
        Assert.Null(notice);
        var raw = JsonSerializer.Deserialize<ClipContent>(ClipCrypto.Open(ClipCrypto.Key(PairingService.HashToken("phone-token")), push.Nonce, push.Data)!,
            ClipProtocol.ContentJson)!;
        Assert.Null(raw.Text);
        Assert.Equal(text.Length, raw.Chars);

        var api = Api();
        var (status, json) = await api.HandleRestAsync(Req("GET", ClipProtocol.Path, "phone-token", $"id={push.Id}"), null, default);
        Assert.Equal(200, status);
        var fetched = ClipMessages.Open(PairingService.HashToken("phone-token"), JsonSerializer.Deserialize<ClipEnvelope>(json, MobileProtocol.Json));
        Assert.Equal(text, fetched?.Text);

        // Not for another device, and gone after ten minutes.
        Assert.Equal(404, (await api.HandleRestAsync(Req("GET", ClipProtocol.Path, otherToken, $"id={push.Id}"), null, default)).Status);
        _time.Advance(ClipProtocol.OfferLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(404, (await api.HandleRestAsync(Req("GET", ClipProtocol.Path, "phone-token", $"id={push.Id}"), null, default)).Status);
    }

    [Fact]
    public async Task A_failed_push_an_unknown_target_and_bad_text_are_reported_per_target()
    {
        var phone = Phone("phone-token", "Galaxy");
        var clips = new ClipShareService(_store, (_, _, _) => Task.FromResult((false, (string?)"EndpointOffline: phone not subscribed")),
            hubs: null, _time, NullLogger.Instance);

        var result = await clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(phone.DeviceId), "device:gone", "pc:nowhere"], "x"), default);
        Assert.Equal(new[] { ClipErrors.PushFailed, ClipErrors.UnknownTarget, ClipErrors.UnknownTarget }, result.Results.Select(r => r.Error));

        var empty = await clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(phone.DeviceId)], ""), default);
        Assert.Equal(ClipErrors.Invalid, Assert.Single(empty.Results).Error);
    }

    [Fact]
    public async Task Push_registration_keeps_the_features_the_app_lists()
    {
        var token = Pair("Galaxy");
        var sub = ProtocolGoldenTests.SampleSubscription();
        var api = Api();

        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", "/api/v1/push", token), Body(sub), default)).Status);
        Assert.True(_store.PushTargets().Single().Subscription.Supports(ClipProtocol.Feature));

        // An app that stops listing it (an older build put back) is no longer offered.
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", "/api/v1/push", token), Body(sub with { Features = null }), default)).Status);
        Assert.False(_store.PushTargets().Single().Subscription.Supports(ClipProtocol.Feature));

        var (status, _) = await api.HandleRestAsync(Req("POST", "/api/v1/push", token), Body(sub with { Features = ["Bad Feature"] }), default);
        Assert.Equal(400, status);
    }
}

/// <summary>
/// Two Agents on loopback (DECISIONS M-62): B sends A clipboard text over its link, through the real socket —
/// longer than the 16 KiB every other request body is held to.
/// </summary>
public class ClipShareEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-clip-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_linked_pc_sends_long_text_to_its_hub_tray()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });

        var storeA = new InMemoryDeviceStore("pc-a");
        var pairingA = new PairingService(storeA, TimeProvider.System);
        var trayA = new List<TrayClip>();
        var clipsA = new ClipShareService(storeA, null, null, TimeProvider.System, NullLogger.Instance);
        var api = new MobileApiServer(
            new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            pairingA, storeA, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance,
            clips: clipsA);

        var storeB = new InMemoryDeviceStore("pc-b");
        var links = new HubLinkService(new HubStore(_dir, NullLogger.Instance, protect: false), storeB,
            new SystemStatusProbeAdapter(h => new PresenceReport(1, DeviceKind.Pc, "0.49.0", PushHashes: h)),
            TimeProvider.System, NullLogger<HubLinkService>.Instance, allowLoopback: true);
        var clipsB = new ClipShareService(storeB, null, links, TimeProvider.System, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await api.StartAsync(cts.Token);
        await links.StartAsync(cts.Token);
        try
        {
            var offer = await Until(() => api.CreateOffer(), cts.Token);
            Assert.True((await links.LinkAsync(PairingLink.Build(offer), cts.Token)).Linked);
            var target = Assert.Single(clipsB.Targets());
            Assert.Equal((ClipTargetKeys.Pc("pc-a"), DeviceKind.Pc), (target.Key, target.Kind));

            // Nobody signed in at A yet.
            var none = await clipsB.SendAsync(new ClipSendRequest([target.Key], "hello"), cts.Token);
            Assert.Equal(ClipErrors.NoUserSession, Assert.Single(none.Results).Error);

            clipsA.Tray = c => { lock (trayA) trayA.Add(c); return true; };
            var text = string.Concat(Enumerable.Repeat("가나다라마바사 0123456789\n", 2000)); // ~50 KB of UTF-8
            var sent = await clipsB.SendAsync(new ClipSendRequest([target.Key], text, Sensitive: true), cts.Token);

            var outcome = Assert.Single(sent.Results);
            Assert.True(outcome.Ok, outcome.Error);
            var got = Assert.Single(trayA);
            Assert.Equal((text, DeviceKind.Pc, true), (got.Text, got.SourceKind, got.Sensitive));
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
