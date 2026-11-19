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
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>The pieces of a transfer (DECISIONS M-64): chunk math, positional sealing, ranges, file names.</summary>
public class TransferPiecesTests
{
    private const int Chunk = TransferProtocol.ChunkBytes;

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData((long)Chunk, 1L)]
    [InlineData((long)Chunk + 1, 2L)]
    [InlineData(3L * Chunk, 3L)]
    public void Chunk_count_covers_the_file(long size, long chunks) => Assert.Equal(chunks, TransferProtocol.ChunkCount(size));

    [Fact]
    public void The_last_chunk_is_the_remainder()
    {
        Assert.Equal(Chunk, TransferProtocol.ChunkLength(Chunk + 5, 0));
        Assert.Equal(5, TransferProtocol.ChunkLength(Chunk + 5, 1));
        Assert.Equal(0, TransferProtocol.ChunkLength(Chunk + 5, 2));
        Assert.Equal(0, TransferProtocol.ChunkLength(0, 0));
        Assert.Equal(0, TransferProtocol.ChunkLength(10, -1));
    }

    [Fact]
    public void Limits_are_checked()
    {
        var ts = DateTimeOffset.UnixEpoch;
        Assert.Null(TransferProtocol.Check(new TransferMeta(1, [new TransferFile("a.jpg", 10, "image/jpeg")], ts)));
        Assert.Equal(TransferErrors.Invalid, TransferProtocol.Check(new TransferMeta(1, [], ts)));
        Assert.Equal(TransferErrors.Invalid, TransferProtocol.Check(new TransferMeta(1, [new TransferFile(" ", 1)], ts)));
        Assert.Equal(TransferErrors.Invalid, TransferProtocol.Check(new TransferMeta(1, [new TransferFile("a", -1)], ts)));
        Assert.Equal(TransferErrors.Invalid, TransferProtocol.Check(new TransferMeta(1, Enumerable.Range(0, 21).Select(i => new TransferFile($"{i}", 1)).ToList(), ts)));
        Assert.Equal(TransferErrors.TooLarge, TransferProtocol.Check(new TransferMeta(1, [new TransferFile("big", TransferProtocol.MaxFileBytes + 1)], ts)));
        Assert.Equal(TransferErrors.TooLarge, TransferProtocol.Check(new TransferMeta(1,
            [new TransferFile("a", TransferProtocol.MaxFileBytes), new TransferFile("b", TransferProtocol.MaxFileBytes), new TransferFile("c", 1)], ts)));
    }

    [Fact]
    public void A_chunk_opens_only_at_its_position_with_its_transfer_key()
    {
        var clipKey = RandomNumberGenerator.GetBytes(32);
        var id = ClipProtocol.NewId();
        var key = TransferCrypto.TransferKey(clipKey, id);
        var sealedChunk = TransferCrypto.SealChunk(key, 2, 7, "hello"u8);

        Assert.Equal("hello"u8.ToArray(), TransferCrypto.OpenChunk(key, 2, 7, sealedChunk));
        Assert.Null(TransferCrypto.OpenChunk(key, 2, 8, sealedChunk));
        Assert.Null(TransferCrypto.OpenChunk(key, 1, 7, sealedChunk));
        Assert.Null(TransferCrypto.OpenChunk(TransferCrypto.TransferKey(clipKey, ClipProtocol.NewId()), 2, 7, sealedChunk));
        Assert.Null(TransferCrypto.OpenChunk(key, 2, 7, sealedChunk.AsSpan(0, 10)));
        Assert.Equal(key, TransferCrypto.TransferKey(clipKey, id));
        Assert.Throws<ArgumentException>(() => TransferCrypto.TransferKey(clipKey, "not-hex"));
    }

    [Theory]
    [InlineData(new long[0], "")]
    [InlineData(new long[] { 0 }, "0")]
    [InlineData(new long[] { 0, 1, 2, 3 }, "0-3")]
    [InlineData(new long[] { 5, 0, 1, 3, 9, 8, 7 }, "0-1,3,5,7-9")]
    public void Ranges_round_trip(long[] chunks, string text)
    {
        Assert.Equal(text, ChunkRanges.Format(chunks));
        Assert.Equal(chunks.Distinct().Order(), ChunkRanges.Parse(text)!);
    }

    [Theory]
    [InlineData("3-1")]
    [InlineData("a")]
    [InlineData("1,,2")]
    [InlineData("-1")]
    public void Bad_ranges_are_refused(string text) => Assert.Null(ChunkRanges.Parse(text));

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("com1", "_com1")]
    [InlineData("report.   ", "report")]
    [InlineData("in‮gnp.exe", "ingnp.exe")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "abcdefgh.txt")]
    [InlineData("tab\there\nnew.txt", "tabherenew.txt")]
    [InlineData("", "file")]
    [InlineData("...", "file")]
    [InlineData(null, "file")]
    public void File_names_are_made_safe_for_windows(string? name, string expected) => Assert.Equal(expected, TransferNames.Clean(name));

    [Fact]
    public void A_long_name_keeps_its_extension_and_a_taken_name_gets_a_number()
    {
        var cleaned = TransferNames.Clean(new string('가', 300) + ".pdf");
        Assert.Equal(200, cleaned.Length);
        Assert.EndsWith(".pdf", cleaned);

        var taken = new HashSet<string> { "a.jpg", "a (2).jpg" };
        Assert.Equal("a (3).jpg", TransferNames.Unique("a.jpg", taken.Contains));
        Assert.Equal("b.jpg", TransferNames.Unique("b.jpg", taken.Contains));
    }
}

/// <summary>The transfer routes and what the PC does with a finished transfer (DECISIONS M-64).</summary>
public class FileShareApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-files-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new("6f1c0d2e9b8a4c7d");
    private readonly InMemoryClipKeys _keys = new();
    private readonly PairingService _pairing;
    private readonly List<TrayTransfer> _tray = new();
    private long _free = 100L * 1024 * 1024 * 1024;
    private FileShareService _files;

    public FileShareApiTests()
    {
        _pairing = new PairingService(_store, _time);
        _files = NewService();
    }

    private FileShareService NewService()
    {
        var files = new FileShareService(_dir, _store, _keys, _time, NullLogger.Instance, freeSpace: _ => _free);
        files.Tray = t => { lock (_tray) _tray.Add(t); return true; };
        return files;
    }

    private MobileApiServer Api()
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        return new MobileApiServer(hub, _pairing, _store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options,
            NullLogger<MobileApiServer>.Instance, files: _files);
    }

    /// <summary>A paired phone with an agreed clipboard key.</summary>
    private (string Token, byte[] Key, MobileDeviceInfo Device) Phone(string name = "Galaxy S24")
    {
        var (code, _) = _pairing.Begin();
        var token = _pairing.TryPair(code, name, null, null).Token!;
        var device = _store.Authenticate(token)!;
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);
        return (token, key, device);
    }

    private static HttpRequestHead Req(string method, string path, string token, string? query = null) =>
        new(method, path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }, query);

    private static byte[] Body<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, MobileProtocol.Json);

    private static TransferMeta Meta(params (string Name, byte[] Bytes)[] files) =>
        new(MobileProtocol.Schema, files.Select(f => new TransferFile(f.Name, f.Bytes.Length, "application/octet-stream")).ToList(), DateTimeOffset.UnixEpoch);

    private static TransferHashes Hashes(params byte[][] files) =>
        new(MobileProtocol.Schema, files.Select(b => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()).ToList());

    /// <summary>Uploads every chunk of every file, as the app does.</summary>
    private async Task SendChunks(MobileApiServer api, string token, byte[] key, string id, params byte[][] files)
    {
        var transferKey = TransferCrypto.TransferKey(key, id);
        for (var f = 0; f < files.Length; f++)
        for (long n = 0; n < TransferProtocol.ChunkCount(files[f].Length); n++)
        {
            var plain = files[f].AsSpan((int)(n * TransferProtocol.ChunkBytes), TransferProtocol.ChunkLength(files[f].Length, n));
            var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.ChunkPath, token, $"id={id}&f={f}&n={n}"),
                TransferCrypto.SealChunk(transferKey, f, n, plain), default);
            Assert.True(status == 200, json);
        }
    }

    [Fact]
    public async Task A_phone_sends_two_files_and_the_tray_is_offered_them()
    {
        var (token, key, device) = Phone();
        var api = Api();
        var id = ClipProtocol.NewId();
        var photo = RandomNumberGenerator.GetBytes(TransferProtocol.ChunkBytes + 1234); // two chunks
        var empty = Array.Empty<byte>();

        var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token),
            Body(TransferMessages.SealMeta(key, id, Meta(("사진.jpg", photo), ("빈 파일.txt", empty)))), default);
        Assert.Equal(200, status);
        var created = JsonSerializer.Deserialize<TransferStatus>(json, MobileProtocol.Json)!;
        Assert.Equal((TransferStates.Receiving, TransferProtocol.ChunkBytes), (created.State, created.ChunkBytes));
        Assert.Equal(new[] { "", "" }, created.Have);

        await SendChunks(api, token, key, id, photo, empty);
        var mid = JsonSerializer.Deserialize<TransferStatus>((await api.HandleRestAsync(Req("GET", TransferProtocol.Path, token, $"id={id}"), null, default)).Json, MobileProtocol.Json)!;
        Assert.Equal(new[] { "0-1", "" }, mid.Have);

        (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, token, $"id={id}"),
            Body(TransferMessages.SealHashes(key, id, Hashes(photo, empty))), default);
        Assert.Equal(200, status);
        Assert.Equal("tray", JsonSerializer.Deserialize<TransferDone>(json, MobileProtocol.Json)!.Saved);

        var offered = Assert.Single(_tray);
        Assert.Equal((id, "Galaxy S24", DeviceKind.Phone), (offered.Id, offered.Source, offered.SourceKind));
        Assert.Equal(new[] { ("사진.jpg", (long)photo.Length), ("빈 파일.txt", 0L) }, offered.Files.Select(f => (f.Name, f.Size)));
        var open = _files.OpenForTray(new TransferPipeRequest(id, 0))!.Value;
        await using (open.Stream)
        {
            using var copy = new MemoryStream();
            await open.Stream.CopyToAsync(copy);
            Assert.Equal(photo, copy.ToArray());
        }

        // Saved by the Tray: gone from the PC's folder.
        _files.Saved(new TrayTransferSaved(id, true));
        Assert.Empty(_files.Ids());
        Assert.False(Directory.Exists(Path.Combine(_dir, id)));
        Assert.Equal(409, (await api.HandleRestAsync(Req("GET", TransferProtocol.Path, token, $"id={id}"), null, default)).Status);
        _ = device;
    }

    [Fact]
    public async Task Without_a_key_the_phone_is_told_to_agree_one()
    {
        var (token, _, device) = Phone();
        var noKey = new InMemoryClipKeys();
        _files = new FileShareService(_dir, _store, noKey, _time, NullLogger.Instance);
        var (status, json) = await Api().HandleRestAsync(Req("POST", TransferProtocol.Path, token),
            Body(TransferMessages.SealMeta(RandomNumberGenerator.GetBytes(32), ClipProtocol.NewId(), Meta(("a", new byte[1])))), default);
        Assert.Equal(409, status);
        Assert.Contains(ClipErrors.KeyNeeded, json);
        _ = device;
    }

    [Fact]
    public async Task Too_large_no_room_and_unreadable_descriptions_are_refused()
    {
        var (token, key, _) = Phone();
        var api = Api();
        var big = new TransferMeta(1, [new TransferFile("big.iso", TransferProtocol.MaxFileBytes + 1)], DateTimeOffset.UnixEpoch);
        Assert.Equal(413, (await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, ClipProtocol.NewId(), big)), default)).Status);

        _free = 1024L * 1024 * 1024; // 1 GiB free: under the 2 GiB margin
        var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token),
            Body(TransferMessages.SealMeta(key, ClipProtocol.NewId(), Meta(("a", new byte[10])))), default);
        Assert.Equal(507, status);
        Assert.Contains(TransferErrors.NoSpace, json);

        _free = 100L * 1024 * 1024 * 1024;
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Encoding.UTF8.GetBytes("{}"), default)).Status);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token),
            Body(TransferMessages.SealMeta(RandomNumberGenerator.GetBytes(32), ClipProtocol.NewId(), Meta(("a", new byte[10])))), default)).Status);
    }

    [Fact]
    public async Task Space_promised_to_transfers_on_the_way_is_not_counted_as_free()
    {
        // 7 GiB free: one 3 GiB transfer leaves the 2 GiB margin; a second, while the first hasn't written anything
        // yet, would not (Codex review on #99).
        var (token, key, _) = Phone();
        var api = Api();
        _free = 7L * 1024 * 1024 * 1024;
        TransferMeta Big() => new(1, [new TransferFile("a.mkv", 3L * 512 * 1024 * 1024), new TransferFile("b.mkv", 3L * 512 * 1024 * 1024)], DateTimeOffset.UnixEpoch);

        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, ClipProtocol.NewId(), Big())), default)).Status);
        var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, ClipProtocol.NewId(), Big())), default);
        Assert.Equal(507, status);
        Assert.Contains(TransferErrors.NoSpace, json);
    }

    [Fact]
    public async Task A_transfer_whose_tray_went_away_is_offered_again()
    {
        var (token, key, _) = Phone();
        var api = Api();
        var id = ClipProtocol.NewId();
        var file = new byte[] { 1, 2, 3 };
        await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, id, Meta(("a.bin", file)))), default);
        await SendChunks(api, token, key, id, file);
        await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, token, $"id={id}"), Body(TransferMessages.SealHashes(key, id, Hashes(file))), default);
        Assert.Single(_tray);

        // Not twice while that Tray may still be saving it…
        _files.OfferWaiting();
        Assert.Single(_tray);
        // …but at once when the bridge sees it go (Codex review on #99), and after the timeout otherwise.
        _files.OfferLost(id);
        _files.OfferWaiting();
        Assert.Equal(2, _tray.Count);
        _time.Advance(TimeSpan.FromMinutes(11));
        _files.OfferWaiting();
        Assert.Equal(3, _tray.Count);
    }

    [Fact]
    public async Task A_chunk_at_the_wrong_place_or_length_is_refused_and_done_waits_for_every_chunk()
    {
        var (token, key, _) = Phone();
        var api = Api();
        var id = ClipProtocol.NewId();
        var file = RandomNumberGenerator.GetBytes(100);
        await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, id, Meta(("a.bin", file)))), default);
        var transferKey = TransferCrypto.TransferKey(key, id);

        var moved = TransferCrypto.SealChunk(transferKey, 0, 1, file);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", TransferProtocol.ChunkPath, token, $"id={id}&f=0&n=0"), moved, default)).Status);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", TransferProtocol.ChunkPath, token, $"id={id}&f=0&n=1"), moved, default)).Status);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", TransferProtocol.ChunkPath, token, $"id={id}&f=0&n=0"),
            TransferCrypto.SealChunk(transferKey, 0, 0, file.AsSpan(0, 50)), default)).Status);

        var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, token, $"id={id}"),
            Body(TransferMessages.SealHashes(key, id, Hashes(file))), default);
        Assert.Equal(409, status);
        Assert.Contains(TransferErrors.Incomplete, json);
        Assert.Empty(_tray);
    }

    [Fact]
    public async Task A_file_that_does_not_match_its_hash_drops_the_transfer()
    {
        var (token, key, _) = Phone();
        var api = Api();
        var id = ClipProtocol.NewId();
        var file = RandomNumberGenerator.GetBytes(100);
        await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, id, Meta(("a.bin", file)))), default);
        await SendChunks(api, token, key, id, file);

        var (status, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, token, $"id={id}"),
            Body(TransferMessages.SealHashes(key, id, Hashes(new byte[100]))), default);
        Assert.Equal(409, status);
        Assert.Contains(TransferErrors.HashMismatch, json);
        Assert.Empty(_files.Ids());
        Assert.Empty(_tray);
    }

    [Fact]
    public async Task Another_device_cannot_see_or_touch_a_transfer()
    {
        var (token, key, _) = Phone();
        var (other, _, _) = Phone("Other");
        var api = Api();
        var id = ClipProtocol.NewId();
        await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, id, Meta(("a", new byte[5])))), default);

        Assert.Equal(409, (await api.HandleRestAsync(Req("GET", TransferProtocol.Path, other, $"id={id}"), null, default)).Status);
        Assert.Equal(409, (await api.HandleRestAsync(Req("DELETE", TransferProtocol.Path, other, $"id={id}"), null, default)).Status);
        Assert.Single(_files.Ids());
    }

    [Fact]
    public async Task Nobody_signed_in_keeps_it_until_a_tray_connects_and_a_restart_keeps_it_too()
    {
        var (token, key, _) = Phone();
        _files.Tray = null;
        var api = Api();
        var id = ClipProtocol.NewId();
        var file = Encoding.UTF8.GetBytes("회의록");
        await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, id, Meta(("회의록.txt", file)))), default);
        await SendChunks(api, token, key, id, file);
        var (_, json) = await api.HandleRestAsync(Req("POST", TransferProtocol.DonePath, token, $"id={id}"),
            Body(TransferMessages.SealHashes(key, id, Hashes(file))), default);
        Assert.Equal("waiting", JsonSerializer.Deserialize<TransferDone>(json, MobileProtocol.Json)!.Saved);

        // The Agent restarts, then a Tray connects: it is offered what waited.
        _files = NewService();
        Assert.Equal(new[] { id }, _files.Ids());
        _files.OfferWaiting();
        Assert.Equal(id, Assert.Single(_tray).Id);

        // A Tray that could not save it gets it again next time; nothing is offered twice meanwhile.
        _files.OfferWaiting();
        Assert.Single(_tray);
        _files.Saved(new TrayTransferSaved(id, false, "disk full"));
        _files.OfferWaiting();
        Assert.Equal(2, _tray.Count);
    }

    [Fact]
    public async Task Unfinished_and_unsaved_transfers_are_dropped_after_a_day_and_cancel_drops_at_once()
    {
        var (token, key, _) = Phone();
        var api = Api();
        var stale = ClipProtocol.NewId();
        var cancelled = ClipProtocol.NewId();
        await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, stale, Meta(("a", new byte[5])))), default);
        await api.HandleRestAsync(Req("POST", TransferProtocol.Path, token), Body(TransferMessages.SealMeta(key, cancelled, Meta(("b", new byte[5])))), default);

        Assert.Equal(200, (await api.HandleRestAsync(Req("DELETE", TransferProtocol.Path, token, $"id={cancelled}"), null, default)).Status);
        Assert.Equal(new[] { stale }, _files.Ids());

        _time.Advance(TransferProtocol.KeepFor - TimeSpan.FromMinutes(1));
        _files.Sweep();
        Assert.Single(_files.Ids());
        _time.Advance(TimeSpan.FromMinutes(2));
        _files.Sweep();
        Assert.Empty(_files.Ids());
        Assert.Empty(Directory.EnumerateDirectories(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The routes through a real socket (DECISIONS M-64): a 4 MiB chunk is read only for a paired device, well past the
/// 16 KiB every other body is held to; then the Tray's side of <c>Homebase.Transfer</c> reads the file back.
/// </summary>
public class FileShareSocketTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-files-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_device_uploads_over_http_and_the_tray_reads_it_over_the_pipe()
    {
        var port = FreePort();
        var options = Options.Create(new MobileOptions { Enabled = true, Tailscale = false, BindLoopback = true, Port = port });
        var store = new InMemoryDeviceStore("pc-a");
        var pairing = new PairingService(store, TimeProvider.System);
        var keys = new InMemoryClipKeys();
        var files = new FileShareService(_dir, store, keys, TimeProvider.System, NullLogger.Instance, freeSpace: _ => long.MaxValue / 2);
        var offered = new TaskCompletionSource<TrayTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
        files.Tray = t => offered.TrySetResult(t);
        var api = new MobileApiServer(
            new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance),
            pairing, store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance, files: files);
        var pipeName = "Homebase.Transfer.Test." + Guid.NewGuid().ToString("N");
        var pipe = new TransferPipeServer(files, options, NullLogger<TransferPipeServer>.Instance, pipeName);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await api.StartAsync(cts.Token);
        await pipe.StartAsync(cts.Token);
        try
        {
            while (api.CreateOffer() is null) await Task.Delay(100, cts.Token);
            var (code, _) = pairing.Begin();
            var token = pairing.TryPair(code, "Galaxy", null, null).Token!;
            var key = RandomNumberGenerator.GetBytes(32);
            keys.Set(ClipTargetKeys.Device(store.Authenticate(token)!.DeviceId), key);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var id = ClipProtocol.NewId();
            var file = RandomNumberGenerator.GetBytes(TransferProtocol.ChunkBytes + 99);
            var meta = new TransferMeta(1, [new TransferFile("video.mp4", file.Length, "video/mp4")], DateTimeOffset.UtcNow);

            // Unpaired: refused before the body is read.
            using (var anonymous = new HttpClient { BaseAddress = http.BaseAddress })
            {
                var refused = await anonymous.PostAsync($"{TransferProtocol.ChunkPath}?id={id}&f=0&n=0", new ByteArrayContent(new byte[1000]), cts.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }

            var created = await http.PostAsync(TransferProtocol.Path, Json(TransferMessages.SealMeta(key, id, meta)), cts.Token);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var transferKey = TransferCrypto.TransferKey(key, id);
            for (long n = 0; n < 2; n++)
            {
                var plain = file.AsSpan((int)(n * TransferProtocol.ChunkBytes), TransferProtocol.ChunkLength(file.Length, n)).ToArray();
                var put = await http.PostAsync($"{TransferProtocol.ChunkPath}?id={id}&f=0&n={n}",
                    new ByteArrayContent(TransferCrypto.SealChunk(transferKey, 0, n, plain)), cts.Token);
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            }
            var done = await http.PostAsync($"{TransferProtocol.DonePath}?id={id}", Json(TransferMessages.SealHashes(key, id,
                new TransferHashes(1, [Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant()]))), cts.Token);
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            Assert.Equal(id, (await offered.Task.WaitAsync(cts.Token)).Id);

            // The Tray's side of the pipe (what FileReceiver does): a bare request, a bare header, the raw bytes.
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000, cts.Token);
            await MobileBridge.WriteFrameAsync(client, (JsonObject)JsonSerializer.SerializeToNode(new TransferPipeRequest(id, 0), MobileProtocol.Json)!, cts.Token);
            var header = (await MobileBridge.ReadFrameAsync(client, cts.Token))!.Deserialize<TransferPipeHeader>(MobileProtocol.Json)!;
            Assert.True(header.Ok);
            Assert.Equal(file.Length, header.Size);
            var received = new byte[header.Size];
            await client.ReadExactlyAsync(received, cts.Token);
            Assert.Equal(file, received);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
            await api.StopAsync(CancellationToken.None);
        }
    }

    private static StringContent Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, MobileProtocol.Json), Encoding.UTF8, "application/json");

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
