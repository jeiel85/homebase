using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Files this PC sends (DECISIONS M-65): to phones by push and fetch.</summary>
public class FileSendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-send-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new("6f1c0d2e9b8a4c7d");
    private readonly InMemoryClipKeys _keys = new();
    private readonly PairingService _pairing;
    private readonly List<(PushTarget Target, byte[] Plaintext)> _pushed = new();
    private readonly List<FilesProgress> _progress = new();
    private bool _pushOk = true;
    private long _free = 100L * 1024 * 1024 * 1024;
    private FileSendService _sends;

    public FileSendTests()
    {
        _pairing = new PairingService(_store, _time);
        _sends = NewService();
    }

    private FileSendService NewService()
    {
        var sends = new FileSendService(_dir, _store, _keys, _time, NullLogger.Instance, (t, p, _) =>
        {
            lock (_pushed) _pushed.Add((t, p));
            return Task.FromResult((_pushOk, _pushOk ? null : (string?)"EndpointOffline"));
        }, freeSpace: _ => _free);
        sends.Progress += p => { lock (_progress) _progress.Add(p); };
        return sends;
    }

    /// <summary>A paired phone that fetches files (<c>share</c>) with an agreed key.</summary>
    private (string Token, byte[] Key, MobileDeviceInfo Device) Phone(string name = "Galaxy S24", bool share = true)
    {
        var (code, _) = _pairing.Begin();
        var token = _pairing.TryPair(code, name, "SM-S921N", null).Token!;
        var device = _store.Authenticate(token)!;
        _store.SetPushSubscription(device.DeviceId, new PushSubscription("https://ntfy.sh/up" + device.DeviceId, "k", "a", _time.GetUtcNow(),
            Features: share ? [ClipProtocol.Feature, FileSendProtocol.Feature] : [ClipProtocol.Feature]));
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);
        return (token, key, device);
    }

    /// <summary>What the Tray does: begin, write each file through the write mode, go.</summary>
    private string Send(IReadOnlyList<string> targets, params (string Name, byte[] Bytes)[] files)
    {
        var begun = _sends.Begin(new FilesBeginRequest(targets, files.Select(f => new TransferFile(f.Name, f.Bytes.Length, "image/jpeg")).ToList()));
        Assert.NotNull(begun.Id);
        for (var i = 0; i < files.Length; i++)
        {
            using (var stream = _sends.OpenForWrite(new TransferPipeRequest(begun.Id!, i, TransferPipeRequest.Write, files[i].Bytes.Length))!)
                stream.Write(files[i].Bytes);
            _sends.Written(begun.Id!, i, Convert.ToHexString(SHA256.HashData(files[i].Bytes)).ToLowerInvariant());
        }
        Assert.True(_sends.Go(begun.Id));
        return begun.Id!;
    }

    private async Task Settled(Func<bool> probe)
    {
        for (var i = 0; i < 100 && !probe(); i++) await Task.Delay(20);
        Assert.True(probe());
    }

    [Fact]
    public void Only_phones_that_fetch_files_and_agreed_a_key_are_offered()
    {
        Phone("Galaxy S24");
        Phone("Old app", share: false);
        var target = Assert.Single(_sends.Targets());
        Assert.Equal(("Galaxy S24", DeviceKind.Phone), (target.Name, target.Kind));
    }

    [Fact]
    public void Begin_checks_targets_limits_and_room()
    {
        var (_, _, phone) = Phone();
        var key = ClipTargetKeys.Device(phone.DeviceId);
        Assert.Equal(TransferErrors.Invalid, _sends.Begin(new FilesBeginRequest(["device:nobody"], [new TransferFile("a", 1)])).Error);
        Assert.Equal(TransferErrors.TooLarge, _sends.Begin(new FilesBeginRequest([key], [new TransferFile("big", TransferProtocol.MaxFileBytes + 1)])).Error);
        _free = 1024L * 1024 * 1024;
        Assert.Equal(TransferErrors.NoSpace, _sends.Begin(new FilesBeginRequest([key], [new TransferFile("a", 10)])).Error);
        _free = 100L * 1024 * 1024 * 1024;
        Assert.NotNull(_sends.Begin(new FilesBeginRequest([key], [new TransferFile("a", 10)])).Id);
    }

    [Fact]
    public void Space_promised_to_sends_still_being_written_is_not_free()
    {
        // 7 GiB free: one 3 GiB send leaves the 2 GiB margin; a second, while the first is still being written from the
        // Tray, would not (Codex review on #100). Once the first is written, its bytes are on disk, not promised.
        var (_, _, phone) = Phone();
        var key = ClipTargetKeys.Device(phone.DeviceId);
        _free = 7L * 1024 * 1024 * 1024;
        TransferFile[] Big() => [new TransferFile("a.mkv", 3L * 512 * 1024 * 1024), new TransferFile("b.mkv", 3L * 512 * 1024 * 1024)];

        Assert.NotNull(_sends.Begin(new FilesBeginRequest([key], Big())).Id);
        Assert.Equal(TransferErrors.NoSpace, _sends.Begin(new FilesBeginRequest([key], Big())).Error);
    }

    [Fact]
    public void Nothing_goes_out_before_every_file_is_written_or_with_a_wrong_size()
    {
        var (_, _, phone) = Phone();
        var id = _sends.Begin(new FilesBeginRequest([ClipTargetKeys.Device(phone.DeviceId)], [new TransferFile("a.jpg", 3), new TransferFile("b.jpg", 2)])).Id!;
        Assert.Null(_sends.OpenForWrite(new TransferPipeRequest(id, 0, TransferPipeRequest.Write, 99)));
        Assert.Null(_sends.OpenForWrite(new TransferPipeRequest(id, 0, null, 3)));
        using (var s = _sends.OpenForWrite(new TransferPipeRequest(id, 0, TransferPipeRequest.Write, 3))!) s.Write([1, 2, 3]);
        _sends.Written(id, 0, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant());

        Assert.False(_sends.Go(id));
        Assert.Empty(_pushed);
    }

    [Fact]
    public async Task A_phone_is_told_by_push_then_fetches_every_chunk_sealed_for_it()
    {
        var (token, key, phone) = Phone();
        var (otherToken, _, _) = Phone("Other");
        var api = Api();
        var photo = RandomNumberGenerator.GetBytes(TransferProtocol.ChunkBytes + 77);
        var note = Encoding.UTF8.GetBytes("회의록");

        var id = Send([ClipTargetKeys.Device(phone.DeviceId)], ("사진.jpg", photo), ("회의록.txt", note));
        await Settled(() => _pushed.Count == 1);

        // The push: kind share, sealed for this phone.
        var push = JsonSerializer.Deserialize<ClipPushEnvelope>(_pushed[0].Plaintext, MobileProtocol.Json)!;
        Assert.Equal((FileSendProtocol.PushKind, id), (push.Kind, push.Id));
        var notice = FileSendMessages.OpenNotice(key, push)!;
        Assert.Equal((2, (long)photo.Length + note.Length, "사진.jpg"), (notice.Count, notice.TotalBytes, notice.Name));
        await Settled(() => _progress.Any(p => p.State == FileSendStates.Notified));

        // The offer: names, sizes and hashes, sealed for this phone.
        var (status, json) = await api.HandleRestAsync(Req("GET", FileSendProtocol.OfferPath, token, $"id={id}"), null, default);
        Assert.Equal(200, status);
        var offer = FileSendMessages.OpenOffer(key, JsonSerializer.Deserialize<ClipEnvelope>(json, MobileProtocol.Json))!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant(), offer.Files[0].Sha256);

        // The chunks: under this phone's transfer key at their positions.
        var transferKey = TransferCrypto.TransferKey(key, id);
        var received = new List<byte>();
        for (long n = 0; n < 2; n++)
        {
            var reply = await _sends.FetchAsync(phone, id, "0", n.ToString(), default);
            received.AddRange(TransferCrypto.OpenChunk(transferKey, 0, n, reply.Bytes!)!);
        }
        Assert.Equal(photo, received.ToArray());
        Assert.Equal(400, (await _sends.FetchAsync(phone, id, "0", "2", default)).Status);

        // Not for another phone, even a paired one.
        Assert.Equal(409, (await api.HandleRestAsync(Req("GET", FileSendProtocol.OfferPath, otherToken, $"id={id}"), null, default)).Status);

        // Received: the send is done and gone.
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", FileSendProtocol.ReceivedPath, token, $"id={id}"),
            Encoding.UTF8.GetBytes("{\"schema\":1}"), default)).Status);
        Assert.Contains(_progress, p => p.State == FileSendStates.Received);
        Assert.Empty(_sends.Ids());
    }

    [Fact]
    public async Task A_push_that_fails_ends_the_send_for_that_phone()
    {
        var (_, _, phone) = Phone();
        _pushOk = false;
        Send([ClipTargetKeys.Device(phone.DeviceId)], ("a.jpg", new byte[5]));
        await Settled(() => _progress.Any(p => p.State == FileSendStates.Failed));
        Assert.Equal(ClipErrors.PushFailed, _progress.Last().Error);
        Assert.Empty(_sends.Ids());
    }

    [Fact]
    public async Task A_send_nobody_fetched_is_dropped_after_a_day_and_a_restart_keeps_it_until_then()
    {
        var (_, _, phone) = Phone();
        var id = Send([ClipTargetKeys.Device(phone.DeviceId)], ("a.jpg", new byte[5]));
        await Settled(() => _progress.Any(p => p.State == FileSendStates.Notified));

        _sends = NewService();
        Assert.Equal(new[] { id }, _sends.Ids());
        _time.Advance(TransferProtocol.KeepFor + TimeSpan.FromMinutes(1));
        _sends.Sweep();
        Assert.Empty(_sends.Ids());
        Assert.Contains(_progress, p => p is { State: FileSendStates.Failed, Error: "expired" });
    }

    private MobileApiServer Api()
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        return new MobileApiServer(hub, _pairing, _store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance, sends: _sends);
    }

    private static HttpRequestHead Req(string method, string path, string token, string? query = null) =>
        new(method, path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }, query);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The sending side through real sockets and pipes (DECISIONS M-65): the Tray's write mode, a phone fetching over HTTP,
/// and one PC uploading to another with its 2a routes.
/// </summary>
public class FileSendEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-send-e2e-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_tray_writes_through_the_pipe_and_a_phone_fetches_over_http()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });
        var store = new InMemoryDeviceStore("pc-a");
        var pairing = new PairingService(store, TimeProvider.System);
        var keys = new InMemoryClipKeys();
        var pushed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = new FileSendService(Path.Combine(_dir, "out"), store, keys, TimeProvider.System, NullLogger.Instance,
            (_, p, _) => { pushed.TrySetResult(p); return Task.FromResult((true, (string?)null)); }, freeSpace: _ => long.MaxValue / 2);
        var files = new FileShareService(Path.Combine(_dir, "in"), store, keys, TimeProvider.System, NullLogger.Instance, freeSpace: _ => long.MaxValue / 2);
        var api = new MobileApiServer(new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            pairing, store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance, files: files, sends: sends);
        var pipeName = "Homebase.Transfer.Test." + Guid.NewGuid().ToString("N");
        var pipe = new TransferPipeServer(files, options, NullLogger<TransferPipeServer>.Instance, pipeName, sends);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await api.StartAsync(cts.Token);
        await pipe.StartAsync(cts.Token);
        try
        {
            while (api.CreateOffer() is null) await Task.Delay(100, cts.Token);
            var (code, _) = pairing.Begin();
            var token = pairing.TryPair(code, "Galaxy", null, null).Token!;
            var phone = store.Authenticate(token)!;
            store.SetPushSubscription(phone.DeviceId, new PushSubscription("https://ntfy.sh/x", "k", "a", DateTimeOffset.UtcNow,
                Features: [FileSendProtocol.Feature]));
            var key = RandomNumberGenerator.GetBytes(32);
            keys.Set(ClipTargetKeys.Device(phone.DeviceId), key);

            // The Tray: begin, the bytes through the pipe's write mode, go.
            var file = RandomNumberGenerator.GetBytes(TransferProtocol.ChunkBytes + 5);
            var id = sends.Begin(new FilesBeginRequest([ClipTargetKeys.Device(phone.DeviceId)], [new TransferFile("clip.png", file.Length, "image/png")])).Id!;
            await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(5000, cts.Token);
                await MobileBridge.WriteFrameAsync(client, (JsonObject)JsonSerializer.SerializeToNode(
                    new TransferPipeRequest(id, 0, TransferPipeRequest.Write, file.Length), MobileProtocol.Json)!, cts.Token);
                Assert.True((await MobileBridge.ReadFrameAsync(client, cts.Token))!.Deserialize<TransferPipeHeader>(MobileProtocol.Json)!.Ok);
                await client.WriteAsync(file, cts.Token);
                Assert.True((await MobileBridge.ReadFrameAsync(client, cts.Token))!.Deserialize<TransferPipeHeader>(MobileProtocol.Json)!.Ok);
            }
            Assert.True(sends.Go(id));
            await pushed.Task.WaitAsync(cts.Token);

            // The phone: offer and chunks over HTTP.
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var offer = FileSendMessages.OpenOffer(key, JsonSerializer.Deserialize<ClipEnvelope>(
                await http.GetStringAsync($"{FileSendProtocol.OfferPath}?id={id}", cts.Token), MobileProtocol.Json))!;
            Assert.Equal(("clip.png", (long)file.Length), (offer.Files[0].Name, offer.Files[0].Size));
            var transferKey = TransferCrypto.TransferKey(key, id);
            var got = new List<byte>();
            for (long n = 0; n < 2; n++)
            {
                using var response = await http.GetAsync($"{FileSendProtocol.FetchPath}?id={id}&f=0&n={n}", cts.Token);
                Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
                got.AddRange(TransferCrypto.OpenChunk(transferKey, 0, n, await response.Content.ReadAsByteArrayAsync(cts.Token))!);
            }
            Assert.Equal(file, got.ToArray());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), offer.Files[0].Sha256);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
            await api.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task One_pc_uploads_to_another_with_its_2a_routes()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });

        // PC B receives (2a).
        var storeB = new InMemoryDeviceStore("pc-b");
        var pairingB = new PairingService(storeB, TimeProvider.System);
        var keysB = new InMemoryClipKeys();
        var filesB = new FileShareService(Path.Combine(_dir, "b-in"), storeB, keysB, TimeProvider.System, NullLogger.Instance, freeSpace: _ => long.MaxValue / 2);
        var offered = new TaskCompletionSource<TrayTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
        filesB.Tray = t => offered.TrySetResult(t);
        var clipsB = new ClipShareService(storeB, keysB, null, null, TimeProvider.System, NullLogger.Instance);
        var apiB = new MobileApiServer(new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            pairingB, storeB, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance, clips: clipsB, files: filesB);

        // PC A sends (2b), linked to B.
        var storeA = new InMemoryDeviceStore("pc-a");
        var keysA = new InMemoryClipKeys();
        var links = new HubLinkService(new HubStore(Path.Combine(_dir, "hubs"), NullLogger.Instance, protect: false), storeA,
            new SystemStatusProbeAdapter(h => new PresenceReport(1, DeviceKind.Pc, "0.51.0", PushHashes: h)),
            TimeProvider.System, NullLogger<HubLinkService>.Instance, allowLoopback: true);
        var clipsA = new ClipShareService(storeA, keysA, null, links, TimeProvider.System, NullLogger.Instance);
        var sendsA = new FileSendService(Path.Combine(_dir, "a-out"), storeA, keysA, TimeProvider.System, NullLogger.Instance,
            hubs: links, clips: clipsA, freeSpace: _ => long.MaxValue / 2);
        var progress = new List<FilesProgress>();
        sendsA.Progress += p => { lock (progress) progress.Add(p); };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await apiB.StartAsync(cts.Token);
        await links.StartAsync(cts.Token);
        try
        {
            var offer = await Until(() => apiB.CreateOffer(), cts.Token);
            Assert.True((await links.LinkAsync(PairingLink.Build(offer), cts.Token)).Linked);
            var target = Assert.Single(sendsA.Targets());

            var files = new[] { RandomNumberGenerator.GetBytes(TransferProtocol.ChunkBytes * 2 + 3), Encoding.UTF8.GetBytes("짧은 메모") };
            var id = sendsA.Begin(new FilesBeginRequest([target.Key], [new TransferFile("영상.mp4", files[0].Length, "video/mp4"),
                new TransferFile("메모.txt", files[1].Length, "text/plain")])).Id!;
            for (var i = 0; i < files.Length; i++)
            {
                using (var s = sendsA.OpenForWrite(new TransferPipeRequest(id, i, TransferPipeRequest.Write, files[i].Length))!) s.Write(files[i]);
                sendsA.Written(id, i, Convert.ToHexString(SHA256.HashData(files[i])).ToLowerInvariant());
            }
            Assert.True(sendsA.Go(id));

            var atB = await offered.Task.WaitAsync(cts.Token);
            Assert.Equal(new[] { "영상.mp4", "메모.txt" }, atB.Files.Select(f => f.Name));
            Assert.Equal(DeviceKind.Pc, atB.SourceKind);
            var open = filesB.OpenForTray(new TransferPipeRequest(atB.Id, 0))!.Value;
            await using (open.Stream)
            {
                using var copy = new MemoryStream();
                await open.Stream.CopyToAsync(copy, cts.Token);
                Assert.Equal(files[0], copy.ToArray());
            }
            await Until(() => progress.FirstOrDefault(p => p.State == FileSendStates.Sent), cts.Token);
            Assert.Empty(sendsA.Ids());
        }
        finally
        {
            await links.StopAsync(CancellationToken.None);
            await apiB.StopAsync(CancellationToken.None);
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
