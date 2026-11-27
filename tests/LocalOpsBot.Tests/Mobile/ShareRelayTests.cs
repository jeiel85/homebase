using System.Security.Cryptography;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>
/// Phone ↔ phone through a PC (DECISIONS M-67): a phone names other phones of the PC in <c>to</c>; the PC opens what it
/// sent and sends it on with its own push or send, sealed for each receiver, with the sender's name.
/// </summary>
public class ShareRelayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-relay-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new("6f1c0d2e9b8a4c7d");
    private readonly InMemoryClipKeys _keys = new();
    private readonly PairingService _pairing;
    private readonly List<(PushTarget Target, byte[] Plaintext)> _pushed = new();
    private readonly List<TrayClip> _trayClips = new();
    private readonly List<TrayTransfer> _trayFiles = new();
    private readonly ClipShareService _clips;
    private readonly FileShareService _files;
    private readonly FileSendService _sends;

    public ShareRelayTests()
    {
        _pairing = new PairingService(_store, _time);
        ClipShareService.PushSender push = (t, p, _) =>
        {
            lock (_pushed) _pushed.Add((t, p));
            return Task.FromResult((true, (string?)null));
        };
        _clips = new ClipShareService(_store, _keys, push, null, _time, NullLogger.Instance)
        {
            Tray = (clip, _) => { lock (_trayClips) _trayClips.Add(clip); return Task.FromResult<TrayClipOutcome?>(TrayClipOutcome.Pasted); },
        };
        _files = new FileShareService(Path.Combine(_dir, "in"), _store, _keys, _time, NullLogger.Instance, freeSpace: _ => long.MaxValue / 2)
        {
            Tray = t => { lock (_trayFiles) _trayFiles.Add(t); return true; },
        };
        _sends = new FileSendService(Path.Combine(_dir, "out"), _store, _keys, _time, NullLogger.Instance, push, freeSpace: _ => long.MaxValue / 2);
        _files.Forwarder = _sends.Adopt;
    }

    private MobileApiServer Api()
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        return new MobileApiServer(hub, _pairing, _store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance,
            clips: _clips, files: _files, sends: _sends);
    }

    /// <summary>A paired phone with an agreed key, reading clip and (unless told) share pushes.</summary>
    private (string Token, byte[] Key, MobileDeviceInfo Device) Phone(string name, bool files = true)
    {
        var (code, _) = _pairing.Begin();
        var token = _pairing.TryPair(code, name, "SM-X", null).Token!;
        var device = _store.Authenticate(token)!;
        _store.SetPushSubscription(device.DeviceId, new PushSubscription("https://ntfy.sh/up" + device.DeviceId, "k", "a", _time.GetUtcNow(),
            Features: files ? [ClipProtocol.Feature, FileSendProtocol.Feature] : [ClipProtocol.Feature]));
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);
        return (token, key, device);
    }

    private static HttpRequestHead Req(string method, string path, string token, string? query = null) =>
        new(method, path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }, query);

    private static byte[] Body<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, MobileProtocol.Json);

    private async Task Settled(Func<bool> probe)
    {
        for (var i = 0; i < 100 && !probe(); i++) await Task.Delay(20);
        Assert.True(probe());
    }

    [Fact]
    public async Task A_phone_sees_the_other_phones_it_can_send_to()
    {
        var (tokenA, _, _) = Phone("Galaxy S24");
        var (_, _, tab) = Phone("Galaxy Tab S9");
        var (_, _, pixel) = Phone("Pixel 8", files: false);
        var (code, _) = _pairing.Begin();
        _pairing.TryPair(code, "No key yet", null, null); // never agreed a key: left out

        var (status, json) = await Api().HandleRestAsync(Req("GET", ShareRelay.PhonesPath, tokenA), null, default);

        Assert.Equal(200, status);
        var phones = JsonSerializer.Deserialize<SharePhones>(json, MobileProtocol.Json)!.Phones;
        Assert.Equal(new[]
        {
            new SharePhone(tab.DeviceId, "Galaxy Tab S9", true, true),
            new SharePhone(pixel.DeviceId, "Pixel 8", true, false),
        }, phones);
        Assert.Equal(401, (await Api().HandleRestAsync(Req("GET", ShareRelay.PhonesPath, "nope"), null, default)).Status);
    }

    [Fact]
    public async Task Text_for_another_phone_goes_on_with_the_senders_name_and_not_onto_this_pcs_clipboard()
    {
        var (tokenA, keyA, _) = Phone("Galaxy S24");
        var (_, keyTab, tab) = Phone("Galaxy Tab S9");
        var content = new ClipContent(MobileProtocol.Schema, "회의실 와이파이: hb-guest", 18, _time.GetUtcNow(), To: [tab.DeviceId, "0000000000000000"]);
        var id = ClipProtocol.NewId();

        var (status, json) = await Api().HandleRestAsync(Req("POST", ClipProtocol.Path, tokenA), Body(ClipMessages.Seal(keyA, id, content)), default);

        Assert.Equal(200, status);
        var accepted = JsonSerializer.Deserialize<ClipAccepted>(json, MobileProtocol.Json)!;
        Assert.Equal(new[] { new ClipForwardOutcome(tab.DeviceId, true), new ClipForwardOutcome("0000000000000000", false, ClipErrors.UnknownTarget) },
            accepted.Forwarded);
        Assert.Empty(_trayClips);
        var (target, plaintext) = Assert.Single(_pushed);
        Assert.Equal(tab.DeviceId, target.DeviceId);
        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(plaintext, MobileProtocol.Json)!;
        var passed = ClipMessages.Open(keyTab, new ClipEnvelope(1, push.Id, push.Nonce, push.Data))!;
        Assert.Equal(("회의실 와이파이: hb-guest", "Galaxy S24"), (passed.Text, passed.From));
        Assert.Null(passed.To);

        // The same message again (a retry): answered, not sent twice.
        (status, _) = await Api().HandleRestAsync(Req("POST", ClipProtocol.Path, tokenA), Body(ClipMessages.Seal(keyA, id, content)), default);
        Assert.Equal(200, status);
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task Long_text_for_another_phone_is_fetched_with_the_senders_name()
    {
        var (tokenA, keyA, _) = Phone("Galaxy S24");
        var (tokenTab, keyTab, tab) = Phone("Galaxy Tab S9");
        var text = string.Concat(Enumerable.Repeat("긴 로그 한 줄입니다.\n", 400));
        var content = new ClipContent(MobileProtocol.Schema, text, text.Length, _time.GetUtcNow(), To: [tab.DeviceId]);

        var (status, _) = await Api().HandleRestAsync(Req("POST", ClipProtocol.Path, tokenA),
            Body(ClipMessages.Seal(keyA, ClipProtocol.NewId(), content)), default);

        Assert.Equal(200, status);
        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(Assert.Single(_pushed).Plaintext, MobileProtocol.Json)!;
        var announced = JsonSerializer.Deserialize<ClipContent>(ClipCrypto.Open(keyTab, push.Nonce, push.Data)!, ClipProtocol.ContentJson)!;
        Assert.Equal((null, "Galaxy S24"), (announced.Text, announced.From)); // too long for the push: only its length
        var (fetched, json) = await Api().HandleRestAsync(Req("GET", ClipProtocol.Path, tokenTab, $"id={push.Id}"), null, default);
        Assert.Equal(200, fetched);
        var opened = ClipMessages.Open(keyTab, JsonSerializer.Deserialize<ClipEnvelope>(json, MobileProtocol.Json))!;
        Assert.Equal((text, "Galaxy S24"), (opened.Text, opened.From));
    }

    [Fact]
    public async Task Text_without_to_still_goes_onto_this_pcs_clipboard()
    {
        var (tokenA, keyA, _) = Phone("Galaxy S24");
        var content = new ClipContent(MobileProtocol.Schema, "hello", 5, _time.GetUtcNow());

        var (status, json) = await Api().HandleRestAsync(Req("POST", ClipProtocol.Path, tokenA),
            Body(ClipMessages.Seal(keyA, ClipProtocol.NewId(), content)), default);

        Assert.Equal((200, "{\"schema\":1,\"accepted\":true}"), (status, json));
        Assert.Equal("hello", Assert.Single(_trayClips).Text);
        Assert.Empty(_pushed);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("")]
    public void A_to_list_that_isnt_device_ids_does_not_open(string bad)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var content = new ClipContent(MobileProtocol.Schema, "x", 1, DateTimeOffset.UnixEpoch, To: [bad]);
        Assert.Null(ClipMessages.Open(key, ClipMessages.Seal(key, ClipProtocol.NewId(), content)));
        Assert.Equal(TransferErrors.Invalid, TransferProtocol.Check(new TransferMeta(1, [new TransferFile("a", 1)], DateTimeOffset.UnixEpoch, [bad])));
        var many = Enumerable.Range(0, ShareRelay.MaxTargets + 1).Select(i => $"phone{i}").ToList();
        Assert.Equal(TransferErrors.Invalid, TransferProtocol.Check(new TransferMeta(1, [new TransferFile("a", 1)], DateTimeOffset.UnixEpoch, many)));
    }

    [Fact]
    public async Task Files_for_another_phone_are_moved_into_a_send_and_announced_with_the_senders_name()
    {
        var (tokenA, keyA, _) = Phone("Galaxy S24");
        var (_, keyTab, tab) = Phone("Galaxy Tab S9");
        var api = Api();
        var id = ClipProtocol.NewId();
        var photo = RandomNumberGenerator.GetBytes(5000);
        var meta = new TransferMeta(MobileProtocol.Schema, [new TransferFile("IMG_0412.jpg", photo.Length, "image/jpeg")], DateTimeOffset.UnixEpoch,
            [tab.DeviceId]);

        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", TransferProtocol.Path, tokenA), Body(TransferMessages.SealMeta(keyA, id, meta)), default)).Status);
        var transferKey = TransferCrypto.TransferKey(keyA, id);
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", TransferProtocol.ChunkPath, tokenA, $"id={id}&f=0&n=0"),
            TransferCrypto.SealChunk(transferKey, 0, 0, photo), default)).Status);
        var hashes = new TransferHashes(MobileProtocol.Schema, [Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant()]);
        var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, tokenA, $"id={id}"),
            Body(TransferMessages.SealHashes(keyA, id, hashes)), default);

        Assert.Equal(200, status);
        Assert.Equal(ShareRelay.Forwarded, JsonSerializer.Deserialize<TransferDone>(json, MobileProtocol.Json)!.Saved);
        Assert.Empty(_trayFiles); // not this PC's
        Assert.Empty(_files.Ids());
        var sendId = Assert.Single(_sends.Ids());
        await Settled(() => _pushed.Count == 1);
        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(_pushed[0].Plaintext, MobileProtocol.Json)!;
        Assert.Equal((FileSendProtocol.PushKind, sendId, tab.DeviceId), (push.Kind, push.Id, _pushed[0].Target.DeviceId));
        var notice = FileSendMessages.OpenNotice(keyTab, push)!;
        Assert.Equal((1, (long)photo.Length, "IMG_0412.jpg", "Galaxy S24"), (notice.Count, notice.TotalBytes, notice.Name, notice.From));

        // The phone missed the answer and starts over as it resumes any upload: create and status say it is all here
        // (no chunk to send again, no new transfer), and done answers forwarded again — nothing goes out twice.
        foreach (var (method, body) in new[] { ("POST", Body(TransferMessages.SealMeta(keyA, id, meta))), ("GET", (byte[]?)null) })
        {
            (status, json) = await api.HandleRestAsync(Req(method, TransferProtocol.Path, tokenA, method == "GET" ? $"id={id}" : null), body, default);
            Assert.Equal(200, status);
            var resumed = JsonSerializer.Deserialize<TransferStatus>(json, MobileProtocol.Json)!;
            Assert.Equal((TransferStates.Complete, "0"), (resumed.State, Assert.Single(resumed.Have)));
        }
        Assert.Empty(_files.Ids());
        (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, tokenA, $"id={id}"),
            Body(TransferMessages.SealHashes(keyA, id, hashes)), default);
        Assert.Equal((200, ShareRelay.Forwarded), (status, JsonSerializer.Deserialize<TransferDone>(json, MobileProtocol.Json)!.Saved));
        Assert.Single(_sends.Ids());
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task Files_for_phones_the_pc_cannot_reach_are_refused_at_done_and_dropped()
    {
        var (tokenA, keyA, _) = Phone("Galaxy S24");
        var (_, _, pixel) = Phone("Pixel 8", files: false); // takes no files
        var api = Api();
        var id = ClipProtocol.NewId();
        var meta = new TransferMeta(MobileProtocol.Schema, [new TransferFile("a.txt", 0)], DateTimeOffset.UnixEpoch, [pixel.DeviceId]);
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", TransferProtocol.Path, tokenA), Body(TransferMessages.SealMeta(keyA, id, meta)), default)).Status);

        var hashes = new TransferHashes(MobileProtocol.Schema, [Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant()]);
        var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, tokenA, $"id={id}"),
            Body(TransferMessages.SealHashes(keyA, id, hashes)), default);

        Assert.Equal(409, status);
        Assert.Contains(ClipErrors.UnknownTarget, json);
        Assert.Empty(_files.Ids());
        Assert.Empty(_sends.Ids());
        Assert.Empty(_trayFiles);
    }

    [Fact]
    public async Task A_phone_cannot_send_files_on_to_itself()
    {
        var (tokenA, keyA, a) = Phone("Galaxy S24");
        var meta = new TransferMeta(MobileProtocol.Schema, [new TransferFile("a.txt", 1)], DateTimeOffset.UnixEpoch, [a.DeviceId]);

        var (status, _) = await Api().HandleRestAsync(Req("POST", TransferProtocol.Path, tokenA),
            Body(TransferMessages.SealMeta(keyA, ClipProtocol.NewId(), meta)), default);

        Assert.Equal(400, status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
