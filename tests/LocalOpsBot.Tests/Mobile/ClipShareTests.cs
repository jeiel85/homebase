using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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

internal sealed class InMemoryClipKeys : IClipKeyStore
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    public byte[]? Get(string key) { lock (_keys) return _keys.TryGetValue(key, out var v) ? v : null; }
    public void Set(string key, byte[] value) { lock (_keys) _keys[key] = value; }
}

/// <summary>The sealed text and its key (DECISIONS M-62): only the agreed key opens it, untouched.</summary>
public class ClipCryptoTests
{
    private static readonly byte[] KeyA = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] KeyB = RandomNumberGenerator.GetBytes(32);

    private static ClipContent Content(string text) =>
        new(MobileProtocol.Schema, text, text.Length, new DateTimeOffset(2026, 11, 19, 4, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Both_sides_of_the_exchange_agree_on_a_key_the_wire_does_not_reveal()
    {
        var (deviceKey, request) = ClipKeys.Begin();
        using (deviceKey)
        {
            var (answer, pcSide) = ClipKeys.Answer(request)!.Value;
            var deviceSide = ClipKeys.Finish(deviceKey, answer);

            Assert.Equal(pcSide, deviceSide);
            Assert.Equal(ClipCrypto.KeyBytes, pcSide.Length);
            // A fresh exchange gives a new key.
            var (other, again) = ClipKeys.Begin();
            using (other) Assert.NotEqual(pcSide, ClipKeys.Answer(again)!.Value.Key);
        }
    }

    [Fact]
    public void A_public_key_that_is_not_a_p256_point_is_refused()
    {
        Assert.Null(ClipKeys.Answer(new ClipKeyExchange(1, Base64Url.Encode(new byte[65]))));
        Assert.Null(ClipKeys.Answer(new ClipKeyExchange(1, "AAAA")));
        Assert.Null(ClipKeys.Answer(new ClipKeyExchange(1, "!!")));
        Assert.Null(ClipKeys.Answer(new ClipKeyExchange(2, LocalOpsBot.Tests.Push.WebPushEncryptionTests.UaPublic)));
        Assert.Null(ClipKeys.Answer(null));
        var (deviceKey, _) = ClipKeys.Begin();
        using (deviceKey) Assert.Null(ClipKeys.Finish(deviceKey, new ClipKeyExchange(1, Base64Url.Encode(new byte[65]))));
    }

    [Fact]
    public void Sealed_text_opens_with_the_same_key_only()
    {
        var envelope = ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content("안녕하세요 😀"));

        Assert.Equal("안녕하세요 😀", ClipMessages.Open(KeyA, envelope)?.Text);
        Assert.Null(ClipMessages.Open(KeyB, envelope));
    }

    [Fact]
    public void What_the_phone_seals_opens_here()
    {
        // The app writes ts with Instant.toString() (UTC "Z", milliseconds when there are any), puts "sensitive"
        // only when true, and keeps non-ASCII as UTF-8 (org.json).
        var plain = Encoding.UTF8.GetBytes("{\"schema\":1,\"text\":\"휴대폰에서 \\\"복사\\\"\\n둘째 줄\",\"chars\":15,\"ts\":\"2026-11-19T04:00:00.123Z\"}");
        var (nonce, data) = ClipCrypto.Seal(KeyA, plain);

        var content = ClipMessages.Open(KeyA, new ClipEnvelope(1, ClipProtocol.NewId(), nonce, data));

        Assert.NotNull(content);
        Assert.Equal("휴대폰에서 \"복사\"\n둘째 줄", content!.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 4, 0, 0, 123, TimeSpan.Zero), content.Ts);
        Assert.Null(content.Sensitive);
    }

    [Fact]
    public void Every_message_gets_its_own_nonce()
    {
        var one = ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content("same"));
        var two = ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content("same"));
        Assert.NotEqual(one.Nonce, two.Nonce);
        Assert.NotEqual(one.Data, two.Data);
    }

    [Fact]
    public void Altered_or_malformed_messages_do_not_open()
    {
        var envelope = ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content("hello"));
        var data = Base64Url.Decode(envelope.Data);
        data[0] ^= 1;

        Assert.Null(ClipMessages.Open(KeyA, envelope with { Data = Base64Url.Encode(data) }));
        Assert.Null(ClipMessages.Open(KeyA, envelope with { Nonce = "AAAA" }));
        Assert.Null(ClipMessages.Open(KeyA, envelope with { Data = "!!" }));
        Assert.Null(ClipMessages.Open(KeyA, envelope with { Id = "not-an-id" }));
        Assert.Null(ClipMessages.Open(KeyA, envelope with { Schema = 2 }));
        Assert.Null(ClipMessages.Open(KeyA, null));
    }

    [Fact]
    public void Empty_or_too_long_text_is_refused_when_opened()
    {
        Assert.Null(ClipMessages.Open(KeyA, ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content(""))));
        var tooLong = new string('x', ClipProtocol.MaxChars + 1);
        Assert.Null(ClipMessages.Open(KeyA, ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content(tooLong))));
        var longest = new string('가', ClipProtocol.MaxChars);
        Assert.Equal(longest, ClipMessages.Open(KeyA, ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content(longest)))?.Text);
    }

    [Fact]
    public void The_longest_text_still_fits_the_request_body_cap()
    {
        // Worst case for the JSON escaping inside: control characters cost 6 bytes each.
        var envelope = ClipMessages.Seal(KeyA, ClipProtocol.NewId(), Content(new string('\u0001', ClipProtocol.MaxChars)));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(envelope, MobileProtocol.Json).Length <= ClipProtocol.MaxBodyBytes);
    }

    [Fact]
    public void A_push_carries_about_a_thousand_korean_characters_and_says_when_it_cannot()
    {
        var push = ClipMessages.PushPlaintext("6f1c0d2e9b8a4c7d", KeyA, ClipProtocol.NewId(), Content(new string('가', 900)));
        Assert.NotNull(push);
        Assert.True(push!.Length <= WebPushEncryption.MaxPlaintextBytes);

        var tooLong = Content(new string('가', 2000));
        Assert.Null(ClipMessages.PushPlaintext("6f1c0d2e9b8a4c7d", KeyA, ClipProtocol.NewId(), tooLong));
        Assert.NotNull(ClipMessages.PushPlaintext("6f1c0d2e9b8a4c7d", KeyA, ClipProtocol.NewId(), tooLong with { Text = null }));
    }
}

/// <summary><c>POST /api/v1/clip/key</c>, <c>POST</c>/<c>GET /api/v1/clip</c> and the Tray's sends (DECISIONS M-62).</summary>
public class ClipShareApiTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new("6f1c0d2e9b8a4c7d");
    private readonly InMemoryClipKeys _keys = new();
    private readonly PairingService _pairing;
    private readonly List<TrayClip> _tray = new();
    private readonly List<(PushTarget Target, byte[] Plaintext)> _pushed = new();
    private readonly ClipShareService _clips;

    public ClipShareApiTests()
    {
        _pairing = new PairingService(_store, _time);
        _clips = new ClipShareService(_store, _keys, (t, p, _) =>
        {
            lock (_pushed) _pushed.Add((t, p));
            return Task.FromResult((true, (string?)null));
        }, hubs: null, _time, NullLogger.Instance)
        {
            Tray = Taken,
        };
    }

    private Task<TrayClipOutcome?> Taken(TrayClip c, CancellationToken _)
    {
        lock (_tray) _tray.Add(c);
        return Task.FromResult<TrayClipOutcome?>(TrayClipOutcome.Pasted);
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

    /// <summary>The device's side of <c>POST /api/v1/clip/key</c> through the API, as the app does it.</summary>
    private async Task<byte[]> Agree(MobileApiServer api, string token)
    {
        var (deviceKey, request) = ClipKeys.Begin();
        using (deviceKey)
        {
            var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.KeyPath, token), Body(request), default);
            Assert.Equal(200, status);
            return ClipKeys.Finish(deviceKey, JsonSerializer.Deserialize<ClipKeyExchange>(json, MobileProtocol.Json))!;
        }
    }

    private ClipEnvelope Sealed(byte[] key, string text, bool sensitive = false, bool quick = false, IReadOnlyList<string>? to = null) =>
        ClipMessages.Seal(key, ClipProtocol.NewId(),
            new ClipContent(MobileProtocol.Schema, text, text.Length, _time.GetUtcNow(), sensitive ? true : null, to, Quick: quick ? true : null));

    private static byte[] Body<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, MobileProtocol.Json);

    private (PushTarget Target, byte[] Key) Phone(string name, bool clip = true, bool keyed = true)
    {
        var device = _store.Add(name, "SM-S921N", PairingService.HashToken(name + "-token"));
        _store.SetPushSubscription(device.DeviceId, new PushSubscription("https://ntfy.sh/up" + device.DeviceId, "k", "a",
            _time.GetUtcNow(), Features: clip ? [ClipProtocol.Feature] : null));
        var key = RandomNumberGenerator.GetBytes(32);
        if (keyed) _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);
        return (_store.PushTargets().Single(t => t.DeviceId == device.DeviceId), key);
    }

    [Fact]
    public async Task Text_from_a_phone_goes_to_the_tray_with_its_sender()
    {
        var token = Pair("Galaxy S24");
        var api = Api();
        var key = await Agree(api, token);

        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(key, "택배 6081-2345", sensitive: true)), default);

        Assert.Equal(200, status);
        Assert.Contains("\"accepted\":true", json);
        // Text sent from the share screen: answered as before quick send (M-78) existed.
        Assert.DoesNotContain("quick", json);
        Assert.DoesNotContain("held", json);
        Assert.Equal(new TrayClip("택배 6081-2345", "Galaxy S24", DeviceKind.Phone, true), Assert.Single(_tray));
    }

    [Fact]
    public async Task Quick_text_reaches_the_tray_marked_quick_and_the_answer_says_it_was_held()
    {
        var token = Pair("Galaxy S24");
        var api = Api();
        var key = await Agree(api, token);
        _clips.Tray = (c, _) =>
        {
            lock (_tray) _tray.Add(c);
            return Task.FromResult<TrayClipOutcome?>(new TrayClipOutcome(true, Held: true));
        };

        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(key, "메모 한 줄", quick: true)), default);

        Assert.Equal(200, status);
        var answer = JsonSerializer.Deserialize<ClipAccepted>(json, MobileProtocol.Json)!;
        Assert.Equal((true, (bool?)true, (bool?)true, (string?)null), (answer.Accepted, answer.Quick, answer.Held, answer.Archiver));
        Assert.DoesNotContain("archiver", json);
        Assert.Equal(new TrayClip("메모 한 줄", "Galaxy S24", DeviceKind.Phone, false, true), Assert.Single(_tray));
    }

    [Fact]
    public async Task Quick_text_on_the_clipboard_says_whether_threads_archiver_runs_and_the_same_message_gets_the_same_answer()
    {
        var token = Pair("Galaxy S24");
        var api = Api();
        var key = await Agree(api, token);
        _clips.Tray = (c, _) =>
        {
            lock (_tray) _tray.Add(c);
            return Task.FromResult<TrayClipOutcome?>(new TrayClipOutcome(true, false, ClipArchiver.Stopped));
        };
        var envelope = Sealed(key, "https://www.threads.com/@zuck/post/DeZtp-TkQHh", quick: true);

        var first = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default);
        var again = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default);

        Assert.Equal(200, first.Status);
        var answer = JsonSerializer.Deserialize<ClipAccepted>(first.Json, MobileProtocol.Json)!;
        Assert.Equal(((bool?)true, (bool?)false, ClipArchiver.Stopped), (answer.Quick, answer.Held, answer.Archiver));
        Assert.Equal((200, first.Json), (again.Status, again.Json));
        Assert.Single(_tray); // pasted once
    }

    [Fact]
    public async Task Held_quick_text_is_answered_as_held_again_when_sent_again()
    {
        var token = Pair("Galaxy S24");
        var api = Api();
        var key = await Agree(api, token);
        _clips.Tray = (_, _) => Task.FromResult<TrayClipOutcome?>(new TrayClipOutcome(true, Held: true));
        var envelope = Sealed(key, "not a link", quick: true);

        await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default);
        _clips.Tray = (_, _) => throw new InvalidOperationException("the same message must not reach the tray twice");
        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default);

        Assert.Equal(200, status);
        Assert.Equal((bool?)true, JsonSerializer.Deserialize<ClipAccepted>(json, MobileProtocol.Json)!.Held);
    }

    [Fact]
    public async Task Quick_text_from_a_linked_pc_or_for_other_phones_is_refused()
    {
        var pcToken = Pair("WORK-PC", DeviceKind.Pc);
        var phoneToken = Pair("Galaxy S24");
        var api = Api();
        var pcKey = await Agree(api, pcToken);
        var phoneKey = await Agree(api, phoneToken);
        var other = _store.List().First(d => d.Name == "WORK-PC").DeviceId;

        var fromPc = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, pcToken), Body(Sealed(pcKey, "x", quick: true)), default);
        var passedOn = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, phoneToken),
            Body(Sealed(phoneKey, "x", quick: true, to: [other])), default);

        Assert.Equal(400, fromPc.Status);
        Assert.Contains(ClipErrors.Invalid, fromPc.Json);
        Assert.Equal(400, passedOn.Status);
        Assert.Empty(_tray);
        // Without quick the same PC's text is still taken.
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, pcToken), Body(Sealed(pcKey, "x")), default)).Status);
    }

    [Fact]
    public async Task A_quick_answer_that_is_not_ok_is_a_busy_clipboard_and_not_remembered()
    {
        var token = Pair("Galaxy S24");
        var api = Api();
        var envelope = Sealed(await Agree(api, token), "https://threads.com/@a/post/b", quick: true);
        _clips.Tray = (_, _) => Task.FromResult<TrayClipOutcome?>(TrayClipOutcome.NotPasted);

        Assert.Equal(503, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        _clips.Tray = Taken;
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Single(_tray);
    }

    [Fact]
    public async Task The_token_alone_cannot_open_or_forge_the_text()
    {
        // The Cloudflare edge sees the bearer token: a key derived from it must not be the agreed one.
        var token = Pair("Galaxy");
        var api = Api();
        var key = await Agree(api, token);
        var fromToken = HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(PairingService.HashToken(token)), 32,
            new byte[32], "homebase clip v1"u8.ToArray());

        Assert.NotEqual(key, fromToken);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(fromToken, "forged")), default)).Status);
        Assert.Empty(_tray);
    }

    [Fact]
    public async Task Without_an_agreed_key_the_sender_is_told_to_agree_one()
    {
        var token = Pair("Galaxy");
        var api = Api();

        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(new byte[32], "x")), default);
        Assert.Equal(409, status);
        Assert.Contains(ClipErrors.KeyNeeded, json);

        // A new exchange replaces the key: text sealed with the old one no longer opens.
        var first = await Agree(api, token);
        var second = await Agree(api, token);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(first, "old")), default)).Status);
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(second, "new")), default)).Status);
    }

    [Fact]
    public async Task The_key_exchange_needs_the_token_and_a_real_point()
    {
        var token = Pair("Galaxy");
        var api = Api();
        var (_, request) = ClipKeys.Begin();

        Assert.Equal(401, (await api.HandleRestAsync(Req("POST", ClipProtocol.KeyPath, "nope"), Body(request), default)).Status);
        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.KeyPath, token),
            Body(new ClipKeyExchange(1, Base64Url.Encode(new byte[65]))), default);
        Assert.Equal(400, status);
        Assert.Contains(ClipErrors.InvalidKey, json);
    }

    [Fact]
    public async Task The_same_message_again_is_pasted_once()
    {
        var token = Pair("Galaxy");
        var api = Api();
        var envelope = Sealed(await Agree(api, token), "once");

        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Single(_tray);

        // Remembered for half an hour, then a new message under that id would be taken again.
        _time.Advance(ClipShareService.SeenFor + TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Equal(2, _tray.Count);
    }

    [Fact]
    public async Task Unpaired_or_malformed_text_is_refused()
    {
        var token = Pair("Galaxy");
        var api = Api();
        var key = await Agree(api, token);

        Assert.Equal(401, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, "nope"), Body(Sealed(key, "x")), default)).Status);
        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Encoding.UTF8.GetBytes("{}"), default);
        Assert.Equal(400, status);
        Assert.Contains(ClipErrors.Invalid, json);
        Assert.Empty(_tray);
    }

    [Fact]
    public async Task Without_a_tray_the_sender_hears_nobody_is_signed_in_and_can_retry()
    {
        var token = Pair("Galaxy");
        var api = Api();
        var envelope = Sealed(await Agree(api, token), "later");
        _clips.Tray = null;

        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default);
        Assert.Equal(409, status);
        Assert.Contains(ClipErrors.NoUserSession, json);

        // Not remembered as taken: the same message is pasted once a tray is back.
        _clips.Tray = Taken;
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Single(_tray);
    }

    [Fact]
    public async Task A_busy_clipboard_is_reported_and_the_same_message_is_tried_again()
    {
        var token = Pair("Galaxy");
        var api = Api();
        var envelope = Sealed(await Agree(api, token), "busy first");
        _clips.Tray = (_, _) => Task.FromResult<TrayClipOutcome?>(TrayClipOutcome.NotPasted);

        var (status, json) = await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default);
        Assert.Equal(503, status);
        Assert.Contains(ClipErrors.ClipboardBusy, json);

        _clips.Tray = Taken;
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(envelope), default)).Status);
        Assert.Equal("busy first", Assert.Single(_tray).Text);
    }

    [Fact]
    public async Task An_agent_without_clipboard_sharing_answers_404()
    {
        var token = Pair("Galaxy");
        var options = Options.Create(new MobileOptions { Enabled = true });
        var api = new MobileApiServer(new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            _pairing, _store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance);
        var (_, request) = ClipKeys.Begin();
        Assert.Equal(404, (await api.HandleRestAsync(Req("POST", ClipProtocol.KeyPath, token), Body(request), default)).Status);
        Assert.Equal(404, (await api.HandleRestAsync(Req("POST", ClipProtocol.Path, token), Body(Sealed(new byte[32], "x")), default)).Status);
    }

    [Fact]
    public void Only_phones_that_read_clipboard_pushes_and_agreed_a_key_are_offered()
    {
        Phone("Galaxy S24");
        Phone("Old phone", clip: false);
        Phone("No key yet", keyed: false);
        Pair("WORK-PC", DeviceKind.Pc); // paired here, but this PC holds no token for it

        var target = Assert.Single(_clips.Targets());
        Assert.Equal(("Galaxy S24", DeviceKind.Phone), (target.Name, target.Kind));
        Assert.StartsWith("device:", target.Key);
    }

    [Fact]
    public async Task Short_text_rides_in_the_push_sealed_for_that_phone()
    {
        var (phone, key) = Phone("Galaxy");

        var result = await _clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(phone.DeviceId)], "회의실 B, 3시", true), default);

        var outcome = Assert.Single(result.Results);
        Assert.True(outcome.Ok);
        Assert.Equal("Galaxy", outcome.Name);
        var (target, plaintext) = Assert.Single(_pushed);
        Assert.Equal(phone.DeviceId, target.DeviceId);
        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(plaintext, MobileProtocol.Json)!;
        Assert.Equal((ClipProtocol.PushKind, "6f1c0d2e9b8a4c7d"), (push.Kind, push.PcId));
        var content = ClipMessages.Open(key, new ClipEnvelope(1, push.Id, push.Nonce, push.Data))!;
        Assert.Equal(("회의실 B, 3시", true), (content.Text, content.Sensitive));
        Assert.Null(ClipMessages.Open(RandomNumberGenerator.GetBytes(32), new ClipEnvelope(1, push.Id, push.Nonce, push.Data)));
    }

    [Fact]
    public async Task Long_text_is_fetched_by_the_phone_it_was_sent_to_within_ten_minutes()
    {
        var phoneToken = Pair("Galaxy");
        var api = Api();
        var key = await Agree(api, phoneToken);
        var phone = _store.List().Single();
        _store.SetPushSubscription(phone.DeviceId, new PushSubscription("https://ntfy.sh/up1", "k", "a", _time.GetUtcNow(),
            Features: [ClipProtocol.Feature]));
        var otherToken = Pair("Other phone");
        await Agree(api, otherToken);
        var text = string.Concat(Enumerable.Repeat("긴 로그 한 줄입니다.\n", 400));

        await _clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(phone.DeviceId)], text), default);

        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(Assert.Single(_pushed).Plaintext, MobileProtocol.Json)!;
        var raw = JsonSerializer.Deserialize<ClipContent>(ClipCrypto.Open(key, push.Nonce, push.Data)!, ClipProtocol.ContentJson)!;
        Assert.Null(raw.Text); // only the length: the phone fetches the text
        Assert.Equal(text.Length, raw.Chars);

        var (status, json) = await api.HandleRestAsync(Req("GET", ClipProtocol.Path, phoneToken, $"id={push.Id}"), null, default);
        Assert.Equal(200, status);
        Assert.Equal(text, ClipMessages.Open(key, JsonSerializer.Deserialize<ClipEnvelope>(json, MobileProtocol.Json))?.Text);

        // Not for another device, and gone after ten minutes.
        Assert.Equal(404, (await api.HandleRestAsync(Req("GET", ClipProtocol.Path, otherToken, $"id={push.Id}"), null, default)).Status);
        _time.Advance(ClipProtocol.OfferLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(404, (await api.HandleRestAsync(Req("GET", ClipProtocol.Path, phoneToken, $"id={push.Id}"), null, default)).Status);
    }

    /// <summary>The notification center's store, as far as clipboard sharing uses it (M-70).</summary>
    private sealed class HistoryLog : LocalOpsBot.Core.Notifications.INotificationHistoryStore
    {
        private readonly List<LocalOpsBot.Core.Notifications.DeviceNotificationRecord> _records = new();

        public IReadOnlyList<LocalOpsBot.Core.Notifications.DeviceNotificationRecord> Records { get { lock (_records) return _records.ToList(); } }

        public Task AddAsync(LocalOpsBot.Core.Notifications.DeviceNotificationRecord record, CancellationToken ct)
        {
            lock (_records) _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<LocalOpsBot.Core.Notifications.HistoryPage> ListAsync(LocalOpsBot.Core.Notifications.HistoryCursor? after, int limit,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ClearDevicesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> PruneAsync(DateTimeOffset cutoff, int maxDeviceEntries, CancellationToken ct) => throw new NotSupportedException();
    }

    private ClipShareService WithHistory(HistoryLog history, ClipShareService.PushSender? push = null, bool? tray = true) =>
        new(_store, _keys, push ?? ((_, _, _) => Task.FromResult((true, (string?)null))), hubs: null, _time, NullLogger.Instance, history)
        {
            Tray = (_, _) => Task.FromResult<TrayClipOutcome?>(tray is null ? null : tray.Value ? TrayClipOutcome.Pasted : TrayClipOutcome.NotPasted),
        };

    [Fact]
    public async Task Quick_text_held_off_the_clipboard_is_kept_as_received_with_that_said()
    {
        var history = new HistoryLog();
        var clips = new ClipShareService(_store, _keys, (_, _, _) => Task.FromResult((true, (string?)null)), hubs: null, _time,
            NullLogger.Instance, history)
        {
            Tray = (_, _) => Task.FromResult<TrayClipOutcome?>(new TrayClipOutcome(true, Held: true)),
        };
        var device = _store.Add("Galaxy S24", "SM-S921N", PairingService.HashToken("t"));
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);

        var receipt = await clips.ReceiveAsync(device, Sealed(key, "회의실 B, 3시", quick: true), default);

        Assert.Equal((ClipAcceptOutcome.Accepted, (bool?)true), (receipt.Outcome, receipt.Quick?.Held));
        var entry = Assert.Single(history.Records);
        Assert.Equal((ClipHistory.Received, "텍스트 9자 · 클립보드에 넣지 않음", "회의실 B, 3시"), (entry.App, entry.Title, entry.Body));
    }

    [Fact]
    public async Task Text_put_on_this_clipboard_is_kept_as_received_once_and_sensitive_text_without_its_words()
    {
        var history = new HistoryLog();
        var clips = WithHistory(history);
        var device = _store.Add("Galaxy S24", "SM-S921N", PairingService.HashToken("t"));
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);

        var envelope = Sealed(key, "회의실 B, 3시");
        Assert.Equal(ClipAcceptOutcome.Accepted, await clips.AcceptAsync(device, envelope, default));
        Assert.Equal(ClipAcceptOutcome.Accepted, await clips.AcceptAsync(device, envelope, default)); // the same message again
        await clips.AcceptAsync(device, Sealed(key, "hunter2", sensitive: true), default);

        Assert.Equal(2, history.Records.Count);
        var plain = history.Records[0];
        Assert.Equal((ClipHistory.Received, device.DeviceId, "Galaxy S24", DeviceKind.Phone), (plain.App, plain.OriginId, plain.OriginName, plain.OriginKind));
        Assert.Equal(("텍스트 9자", "회의실 B, 3시"), (plain.Title, plain.Body));
        var secret = history.Records[1];
        Assert.Equal(("텍스트 7자 · 민감한 내용이라 보관 안 함", null), (secret.Title, secret.Body));
    }

    [Fact]
    public async Task Text_this_pc_could_not_put_on_the_clipboard_is_not_kept()
    {
        var history = new HistoryLog();
        var device = _store.Add("Galaxy", "SM-S921N", PairingService.HashToken("t"));
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);

        Assert.Equal(ClipAcceptOutcome.ClipboardBusy, await WithHistory(history, tray: false).AcceptAsync(device, Sealed(key, "x"), default));
        Assert.Equal(ClipAcceptOutcome.NoUserSession, await WithHistory(history, tray: null).AcceptAsync(device, Sealed(key, "x"), default));
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task Text_sent_is_kept_once_per_device_with_how_it_went()
    {
        var (ok, _) = Phone("Galaxy");
        var (offline, _) = Phone("Tab");
        var history = new HistoryLog();
        var clips = WithHistory(history, (t, _, _) =>
            Task.FromResult(t.DeviceId == ok.DeviceId ? (true, (string?)null) : (false, "EndpointOffline")));

        await clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(ok.DeviceId), ClipTargetKeys.Device(offline.DeviceId), "device:gone"],
            "주소: 서울시 중구"), default);
        await clips.SendAsync(new ClipSendRequest([ClipTargetKeys.Device(ok.DeviceId)], "pw!", Sensitive: true), default);

        var records = history.Records;
        Assert.Equal(3, records.Count); // the target no longer paired has no entry
        Assert.All(records, r => Assert.Equal(ClipHistory.Sent, r.App));
        Assert.Equal(("Galaxy", ok.DeviceId, "텍스트 10자 · 보냄", "주소: 서울시 중구"), (records[0].OriginName, records[0].OriginId, records[0].Title, records[0].Body));
        Assert.Equal(("Tab", "텍스트 10자 · 실패: 휴대폰에 알리지 못함"), (records[1].OriginName, records[1].Title));
        Assert.Equal(("텍스트 3자 · 민감한 내용이라 보관 안 함 · 보냄", null), (records[2].Title, records[2].Body));
    }

    [Fact]
    public void Long_text_is_kept_up_to_its_first_two_thousand_characters_never_half_a_pair()
    {
        var text = new string('가', ClipHistory.KeptChars + 10);
        Assert.Equal(ClipHistory.KeptChars, ClipHistory.Body(text, false)!.Length);
        Assert.Equal("텍스트 2,010자 · 앞 2,000자만 보관", ClipHistory.Title(text.Length, false));

        var emoji = new string('a', ClipHistory.KeptChars - 1) + "😀" + "tail";
        var kept = ClipHistory.Body(emoji, false)!;
        Assert.Equal(ClipHistory.KeptChars - 1, kept.Length);
        Assert.False(char.IsHighSurrogate(kept[^1]));
        Assert.Null(ClipHistory.Body("secret", sensitive: true));
    }

    [Fact]
    public async Task A_failed_push_an_unknown_target_and_bad_text_are_reported_per_target()
    {
        var (phone, _) = Phone("Galaxy");
        var clips = new ClipShareService(_store, _keys, (_, _, _) => Task.FromResult((false, (string?)"EndpointOffline: phone not subscribed")),
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
/// Two Agents on loopback (DECISIONS M-62): B agrees a key with A and sends it clipboard text over its link, through
/// the real socket — longer than the 16 KiB every other request body is held to.
/// </summary>
public class ClipShareEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-clip-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_linked_pc_agrees_a_key_and_sends_long_text_to_its_hub_tray()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });

        var storeA = new InMemoryDeviceStore("pc-a");
        var pairingA = new PairingService(storeA, TimeProvider.System);
        var keysA = new InMemoryClipKeys();
        var trayA = new List<TrayClip>();
        var clipsA = new ClipShareService(storeA, keysA, null, null, TimeProvider.System, NullLogger.Instance);
        var api = new MobileApiServer(
            new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            pairingA, storeA, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance,
            clips: clipsA);

        var storeB = new InMemoryDeviceStore("pc-b");
        var keysB = new InMemoryClipKeys();
        var links = new HubLinkService(new HubStore(_dir, NullLogger.Instance, protect: false), storeB,
            new SystemStatusProbeAdapter(h => new PresenceReport(1, DeviceKind.Pc, "0.49.0", PushHashes: h)),
            TimeProvider.System, NullLogger<HubLinkService>.Instance, allowLoopback: true);
        var clipsB = new ClipShareService(storeB, keysB, null, links, TimeProvider.System, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await api.StartAsync(cts.Token);
        await links.StartAsync(cts.Token);
        try
        {
            var offer = await Until(() => api.CreateOffer(), cts.Token);
            Assert.True((await links.LinkAsync(PairingLink.Build(offer), cts.Token)).Linked);
            var target = Assert.Single(clipsB.Targets());
            Assert.Equal((ClipTargetKeys.Pc("pc-a"), DeviceKind.Pc), (target.Key, target.Kind));

            // Nobody signed in at A yet — but the key is agreed on the way.
            var none = await clipsB.SendAsync(new ClipSendRequest([target.Key], "hello"), cts.Token);
            Assert.Equal(ClipErrors.NoUserSession, Assert.Single(none.Results).Error);
            var keyAtB = keysB.Get(ClipTargetKeys.Pc("pc-a"));
            Assert.NotNull(keyAtB);
            Assert.Equal(keyAtB, keysA.Get(ClipTargetKeys.Device(Assert.Single(storeA.List()).DeviceId)));

            clipsA.Tray = (c, _) => { lock (trayA) trayA.Add(c); return Task.FromResult<TrayClipOutcome?>(TrayClipOutcome.Pasted); };
            var text = string.Concat(Enumerable.Repeat("가나다라마바사 0123456789\n", 2000)); // ~50 KB of UTF-8
            var sent = await clipsB.SendAsync(new ClipSendRequest([target.Key], text, Sensitive: true), cts.Token);

            var outcome = Assert.Single(sent.Results);
            Assert.True(outcome.Ok, outcome.Error);
            var got = Assert.Single(trayA);
            Assert.Equal((text, DeviceKind.Pc, true), (got.Text, got.SourceKind, got.Sensitive));

            // A was set up again and lost its key: B agrees a new one once and the text still arrives.
            keysA.Set(ClipTargetKeys.Device(Assert.Single(storeA.List()).DeviceId), RandomNumberGenerator.GetBytes(32));
            var again = await clipsB.SendAsync(new ClipSendRequest([target.Key], "after a reset"), cts.Token);
            Assert.True(Assert.Single(again.Results).Ok);
            Assert.Equal("after a reset", trayA[^1].Text);
            Assert.NotEqual(keyAtB, keysB.Get(ClipTargetKeys.Pc("pc-a")));
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
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
