using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>An answer for the API server to write as is.</summary>
public readonly record struct TransferReply(int Status, string Json);

/// <summary>
/// Files a paired device sends to this PC (DECISIONS M-64, <c>docs/FILE_SHARE.md</c> 2a). Each transfer lives in its own
/// folder under <c>ProgramData\Homebase\mobile\transfers</c> (SYSTEM and Administrators only, inherited from the mobile
/// folder): <c>transfer.json</c> with what arrived, and one <c>&lt;index&gt;.bin</c> per file, decrypted chunk by chunk.
/// Once every file matches its hash, the Tray of the signed-in user reads them over <see cref="TransferPipe"/> and saves
/// them in that user's Downloads — the Agent (SYSTEM) never writes to a user's folder or opens a path a user chose.
/// Nothing finished or saved is kept past <see cref="TransferProtocol.KeepFor"/>.
/// </summary>
public sealed class FileShareService : BackgroundService
{
    /// <summary>Everything kept here at once (unfinished and unsaved transfers together), unless set (<c>mobile:filesStoreGiB</c>).</summary>
    internal const long StoreCapBytes = 10L * 1024 * 1024 * 1024;
    /// <summary>Free disk left over after a transfer, at least.</summary>
    internal const long FreeMarginBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>Chunks decrypted and written at once (each holds one 4 MiB body in memory).</summary>
    internal const int ConcurrentChunks = 4;
    /// <summary>How long a chunk waits for its turn before the device is told "busy".</summary>
    internal static readonly TimeSpan ChunkWait = TimeSpan.FromSeconds(30);
    /// <summary>How often old transfers are dropped and finished ones nobody answered for are offered again.</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);
    private const int MaxActivePerDevice = 4;
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromMinutes(10);

    private readonly string _root;
    private readonly long _storeCap;
    private readonly IMobileDeviceStore _devices;
    private readonly IClipKeyStore _keys;
    private readonly INotificationHistoryStore? _history;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<string, long>? _freeSpace;
    private readonly SemaphoreSlim _chunkGate = new(ConcurrentChunks, ConcurrentChunks);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _createGate = new();

    /// <summary>The state kept in <c>transfer.json</c>.</summary>
    internal sealed class TransferState
    {
        public string Id { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string DeviceKind { get; set; } = "";
        public List<TransferFile> Files { get; set; } = new();
        public DateTimeOffset Ts { get; set; }
        /// <summary>Per file, the chunks that arrived (<see cref="ChunkRanges"/>).</summary>
        public List<string> Have { get; set; } = new();
        public string State { get; set; } = TransferStates.Receiving;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        /// <summary>Other phones to pass the files on to (M-67), instead of the Tray; null for this PC.</summary>
        public List<string>? To { get; set; }
        /// <summary>The checked SHA-256 of each file, once complete (kept for passing them on).</summary>
        public List<string>? Sha256 { get; set; }
    }

    private sealed class Entry(TransferState state, string dir)
    {
        public TransferState State { get; } = state;
        public string Dir { get; } = dir;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public List<HashSet<long>> Have { get; } = state.Have.Select(r => (ChunkRanges.Parse(r) ?? Array.Empty<long>()).ToHashSet()).ToList();
        public long TotalBytes => State.Files.Sum(f => f.Size);

        /// <summary>Bytes still to arrive: a transfer on the way has only empty files until its chunks come.</summary>
        public long OutstandingBytes => State.State != TransferStates.Receiving
            ? 0
            : State.Files.Select((f, i) => f.Size - Have[i].Sum(n => (long)TransferProtocol.ChunkLength(f.Size, n))).Sum();
        /// <summary>
        /// When it was handed to a Tray that has not answered yet. Not offered again meanwhile, unless that Tray stays
        /// silent past <see cref="OfferTimeout"/> (it went away while saving).
        /// </summary>
        public DateTimeOffset? OfferedAt;
    }

    public FileShareService(string root, IMobileDeviceStore devices, IClipKeyStore keys, TimeProvider time, ILogger logger,
        INotificationHistoryStore? history = null, Func<string, long>? freeSpace = null, long storeCapBytes = StoreCapBytes)
    {
        _storeCap = storeCapBytes;
        _root = root;
        _devices = devices;
        _keys = keys;
        _time = time;
        _logger = logger;
        _history = history;
        _freeSpace = freeSpace;
        Directory.CreateDirectory(_root);
        Load();
    }

    /// <summary>
    /// Hands finished files to a connected Tray that saves files (set by the mobile bridge). Returns whether one was
    /// there to take them; the Tray answers <see cref="Saved"/> once it has saved them (or could not).
    /// </summary>
    public Func<TrayTransfer, bool>? Tray { get; set; }

    /// <summary>
    /// Takes finished files meant for other phones (M-67, set to <see cref="FileSendService.Adopt"/>): null when they are
    /// on their way, else why not. The files are moved away by it.
    /// </summary>
    public Func<ForwardedFiles, string?>? Forwarder { get; set; }

    /// <summary>Transfers already passed on, by device and id, so a repeated <c>done</c> gets the same answer.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _forwarded = new(StringComparer.Ordinal);

    // ── API (DECISIONS M-64) ────────────────────────────────────────────────────

    /// <summary><c>POST /api/v1/transfer</c>: a new transfer (or the same one again: its status).</summary>
    public TransferReply Create(MobileDeviceInfo device, ClipEnvelope? envelope)
    {
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return Error(409, ClipErrors.KeyNeeded, "Agree a clipboard key first.");
        if (envelope is { Id: { } existingId } && ClipProtocol.IsValidId(existingId) && _entries.TryGetValue(existingId, out var known))
            return known.State.DeviceId == device.DeviceId ? Ok(Status(known)) : Error(400, TransferErrors.Invalid, "Pick another transfer id.");
        var meta = TransferMessages.OpenMeta(key, envelope);
        if (meta is null) return Error(400, TransferErrors.Invalid, "The transfer description doesn't open with the agreed key.");
        if (meta.To is { } to && (Forwarder is null || to.Contains(device.DeviceId)))
            return Error(400, TransferErrors.Invalid, "Files can be passed on to other phones only.");
        if (TransferProtocol.Check(meta) is { } problem)
            return problem == TransferErrors.TooLarge
                ? Error(413, problem, "Up to 20 files, 2 GiB each and 4 GiB together.")
                : Error(400, problem, "Invalid transfer description.");

        var total = meta.Files.Sum(f => f.Size);
        lock (_createGate)
        {
            if (_entries.TryGetValue(envelope!.Id, out var raced)) return Ok(Status(raced));
            if (_entries.Values.Count(e => e.State.DeviceId == device.DeviceId && e.State.State == TransferStates.Receiving) >= MaxActivePerDevice)
                return Error(503, TransferErrors.Busy, "Finish or cancel the transfers already on the way.");
            var inUse = _entries.Values.Sum(e => e.TotalBytes);
            // Transfers already accepted will still write what they haven't: that space is promised, not free.
            var free = FreeSpace() - _entries.Values.Sum(e => e.OutstandingBytes);
            if (inUse + total > _storeCap || free < total + FreeMarginBytes)
            {
                _logger.LogWarning("Transfer from {Name} refused: {Total} bytes, {InUse} kept, {Free} free.", device.Name, total, inUse, free);
                return Error(507, TransferErrors.NoSpace, "Not enough room on this PC for these files.");
            }

            var state = new TransferState
            {
                Id = envelope.Id, DeviceId = device.DeviceId, DeviceName = device.Name, DeviceKind = device.Kind,
                Files = meta.Files.ToList(), Ts = meta.Ts, Have = meta.Files.Select(_ => "").ToList(), CreatedAt = _time.GetUtcNow(),
                To = meta.To?.Distinct(StringComparer.Ordinal).ToList(),
            };
            var dir = Path.Combine(_root, envelope.Id);
            Directory.CreateDirectory(dir);
            // Every file exists from the start, so an empty one needs no chunk and a hash check finds them all.
            for (var i = 0; i < state.Files.Count; i++) File.Create(FilePath(dir, i)).Dispose();
            var entry = new Entry(state, dir);
            Save(entry);
            _entries[envelope.Id] = entry;
            _logger.LogInformation("Transfer {Id} from {Name}: {Count} file(s), {Total} bytes.", envelope.Id, device.Name, state.Files.Count, total);
            return Ok(Status(entry));
        }
    }

    /// <summary><c>GET /api/v1/transfer?id=</c>.</summary>
    public TransferReply Get(MobileDeviceInfo device, string? id) =>
        Find(device, id) is { } entry ? Ok(Status(entry)) : Unknown();

    /// <summary><c>POST /api/v1/transfer/chunk?id=&amp;f=&amp;n=</c>: decrypts and writes one chunk.</summary>
    public async Task<TransferReply> ChunkAsync(MobileDeviceInfo device, string? id, string? f, string? n, byte[]? body, CancellationToken ct)
    {
        if (Find(device, id) is not { } entry) return Unknown();
        if (!int.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var file) || file >= entry.State.Files.Count
            || !long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var chunk))
            return Error(400, TransferErrors.BadChunk, "No such chunk.");
        var length = TransferProtocol.ChunkLength(entry.State.Files[file].Size, chunk);
        if (length == 0 || body is null || body.Length != length + ClipCrypto.TagBytes)
            return Error(400, TransferErrors.BadChunk, "No such chunk, or the wrong length.");
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return Error(409, ClipErrors.KeyNeeded, "Agree a clipboard key first.");
        var plain = TransferCrypto.OpenChunk(TransferCrypto.TransferKey(key, entry.State.Id), file, chunk, body);
        if (plain is null) return Error(400, TransferErrors.BadChunk, "The chunk doesn't open at that position.");

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (entry.State.State != TransferStates.Receiving) return Ok(Status(entry)); // a late duplicate
            await using (var stream = new FileStream(FilePath(entry.Dir, file), FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.Position = chunk * TransferProtocol.ChunkBytes;
                await stream.WriteAsync(plain, ct);
            }
            entry.Have[file].Add(chunk);
            entry.State.Have[file] = ChunkRanges.Format(entry.Have[file]);
            Save(entry);
        }
        finally
        {
            entry.Lock.Release();
        }
        return new TransferReply(200, "{\"schema\":1,\"ok\":true}");
    }

    /// <summary>
    /// <c>POST /api/v1/transfer/done?id=</c>: checks every file against its hash, then hands them to the Tray. A file
    /// that doesn't match drops the whole transfer (it changed while it was sent).
    /// </summary>
    public async Task<TransferReply> DoneAsync(MobileDeviceInfo device, string? id, ClipEnvelope? envelope, CancellationToken ct)
    {
        if (id is not null && _forwarded.ContainsKey(device.DeviceId + ":" + id))
            return Ok(new TransferDone(MobileProtocol.Schema, ShareRelay.Forwarded));
        if (Find(device, id) is not { } entry) return Unknown();
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return Error(409, ClipErrors.KeyNeeded, "Agree a clipboard key first.");
        if (envelope?.Id != entry.State.Id || TransferMessages.OpenHashes(key, envelope) is not { } hashes
            || hashes.Sha256.Count != entry.State.Files.Count)
            return Error(400, TransferErrors.Invalid, "The hashes don't open with the agreed key.");

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (entry.State.State == TransferStates.Receiving)
            {
                for (var i = 0; i < entry.State.Files.Count; i++)
                    if (entry.Have[i].Count != TransferProtocol.ChunkCount(entry.State.Files[i].Size))
                        return Error(409, TransferErrors.Incomplete, "Some chunks have not arrived; ask for the status.");
                for (var i = 0; i < entry.State.Files.Count; i++)
                {
                    await using var stream = new FileStream(FilePath(entry.Dir, i), FileMode.Open, FileAccess.Read, FileShare.Read);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                    if (stream.Length == entry.State.Files[i].Size && actual == hashes.Sha256[i]) continue;
                    _logger.LogWarning("Transfer {Id} from {Name}: file {Index} doesn't match its hash; dropped.", entry.State.Id, device.Name, i);
                    stream.Close();
                    Remove(entry);
                    return Error(409, TransferErrors.HashMismatch, "A file doesn't match its hash. Send it again.");
                }
                entry.State.State = TransferStates.Complete;
                entry.State.CompletedAt = _time.GetUtcNow();
                entry.State.Sha256 = hashes.Sha256.ToList();
                Save(entry);
                // Files passed on are kept in the notification center as sent, by the send service.
                if (entry.State.To is null) await RecordAsync(entry);
                _logger.LogInformation("Transfer {Id} from {Name} complete: {Count} file(s), {Total} bytes.",
                    entry.State.Id, device.Name, entry.State.Files.Count, entry.TotalBytes);
            }
            if (entry.State.To is { Count: > 0 }) return Forward(entry);
        }
        finally
        {
            entry.Lock.Release();
        }
        var saving = Offer(entry);
        return Ok(new TransferDone(MobileProtocol.Schema, saving ? "tray" : "waiting"));
    }

    /// <summary>Finished files for other phones (M-67): handed to the send service, which moves them; nothing stays here.</summary>
    private TransferReply Forward(Entry entry)
    {
        var state = entry.State;
        var paths = state.Files.Select((_, i) => FilePath(entry.Dir, i)).ToList();
        var error = Forwarder is { } forward
            ? forward(new ForwardedFiles(state.Id, state.Files, paths, state.Sha256 ?? new List<string>(), state.To!, state.DeviceName, state.DeviceId))
            : ClipErrors.UnknownTarget;
        Remove(entry);
        if (error is null)
        {
            _forwarded[state.DeviceId + ":" + state.Id] = _time.GetUtcNow();
            _logger.LogInformation("Transfer {Id} from {Name} passed on to {Count} phone(s).", state.Id, state.DeviceName, state.To!.Count);
            return Ok(new TransferDone(MobileProtocol.Schema, ShareRelay.Forwarded));
        }
        _logger.LogWarning("Transfer {Id} from {Name} could not be passed on: {Error}.", state.Id, state.DeviceName, error);
        return error == TransferErrors.NoSpace
            ? Error(507, error, "Not enough room on this PC to pass these files on.")
            : Error(409, error, "The phones to pass these files on to are not reachable from this PC.");
    }

    /// <summary><c>DELETE /api/v1/transfer?id=</c>: the device gave up; what arrived is deleted.</summary>
    public TransferReply Cancel(MobileDeviceInfo device, string? id)
    {
        if (Find(device, id) is not { } entry) return Unknown();
        Remove(entry);
        _logger.LogInformation("Transfer {Id} cancelled by {Name}.", entry.State.Id, device.Name);
        return new TransferReply(200, "{\"schema\":1,\"ok\":true}");
    }

    /// <summary>Waits for a turn to write a chunk; false when the device should come back later (503 busy).</summary>
    public async Task<bool> EnterChunkAsync(CancellationToken ct) => await _chunkGate.WaitAsync(ChunkWait, ct);

    public void LeaveChunk() => _chunkGate.Release();

    // ── Tray (bridge and Homebase.Transfer) ────────────────────────────────────

    /// <summary>A Tray that saves files just connected: offer it what waits (finished while nobody was signed in).</summary>
    public void OfferWaiting()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _entries.Values.Where(e => e.State.State == TransferStates.Complete && e.State.To is null
                     && (e.OfferedAt is not { } at || now - at > OfferTimeout)))
            Offer(entry);
    }

    /// <summary>
    /// The Tray a transfer was handed to went away before answering (the bridge saw its connection close): offer it
    /// again at once instead of waiting out <see cref="OfferTimeout"/>.
    /// </summary>
    public void OfferLost(string id)
    {
        if (_entries.TryGetValue(id, out var entry)) entry.OfferedAt = null;
    }

    /// <summary>The Tray's answer to <c>transfer.ready</c>: saved — the files are deleted here; not saved — offered again later.</summary>
    public void Saved(TrayTransferSaved? saved)
    {
        if (saved is null || !_entries.TryGetValue(saved.Id, out var entry)) return;
        entry.OfferedAt = null;
        if (saved.Ok)
        {
            Remove(entry);
            _logger.LogInformation("Transfer {Id} from {Name} saved by the Tray.", entry.State.Id, entry.State.DeviceName);
        }
        else
        {
            _logger.LogWarning("Transfer {Id} from {Name} could not be saved by the Tray: {Error}. Kept until a Tray connects again.",
                entry.State.Id, entry.State.DeviceName, saved.Error ?? "unknown");
        }
    }

    /// <summary>A finished file for the Tray to read, or null (not finished, no such file).</summary>
    public (FileStream Stream, long Size)? OpenForTray(TransferPipeRequest? request)
    {
        if (request is null || !_entries.TryGetValue(request.Id, out var entry) || entry.State.State != TransferStates.Complete
            || request.File < 0 || request.File >= entry.State.Files.Count)
            return null;
        var stream = new FileStream(FilePath(entry.Dir, request.File), FileMode.Open, FileAccess.Read, FileShare.Read);
        return (stream, stream.Length);
    }

    // ── Housekeeping ───────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sweep();
                // Finished transfers whose Tray never answered (it hung, or left without the bridge noticing) go
                // to a connected Tray again once OfferTimeout has passed — not only when a Tray reconnects.
                OfferWaiting();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Sweeping old transfers failed"); }
            try { await Task.Delay(SweepEvery, _time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Drops what is older than <see cref="TransferProtocol.KeepFor"/>: unfinished since it began, unsaved since it finished.</summary>
    internal void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, at) in _forwarded)
            if (now - at > TransferProtocol.KeepFor) _forwarded.TryRemove(key, out _);
        foreach (var entry in _entries.Values.ToList())
        {
            var since = entry.State.CompletedAt ?? entry.State.CreatedAt;
            if (now - since <= TransferProtocol.KeepFor) continue;
            Remove(entry);
            _logger.LogInformation("Transfer {Id} from {Name} dropped after {Hours} h ({State}).", entry.State.Id, entry.State.DeviceName,
                (int)TransferProtocol.KeepFor.TotalHours, entry.State.State);
        }
    }

    internal IReadOnlyList<string> Ids() => _entries.Keys.ToList();

    // ── Internals ──────────────────────────────────────────────────────────────

    private bool Offer(TrayTransfer transfer, Entry entry)
    {
        entry.OfferedAt = _time.GetUtcNow();
        if (Tray?.Invoke(transfer) == true) return true;
        entry.OfferedAt = null;
        return false;
    }

    private bool Offer(Entry entry) =>
        Offer(new TrayTransfer(entry.State.Id, entry.State.DeviceName, entry.State.DeviceKind,
            entry.State.Files.Select((f, i) => new TrayTransferFile(i, f.Name, f.Size, f.Mime)).ToList()), entry);

    private Entry? Find(MobileDeviceInfo device, string? id) =>
        ClipProtocol.IsValidId(id) && _entries.TryGetValue(id!, out var entry) && entry.State.DeviceId == device.DeviceId ? entry : null;

    private static TransferStatus Status(Entry e) =>
        new(MobileProtocol.Schema, e.State.Id, e.State.State, TransferProtocol.ChunkBytes, e.State.Have.ToList());

    private async Task RecordAsync(Entry entry)
    {
        if (_history is null) return;
        var files = entry.State.Files;
        var title = $"파일 {files.Count}개 · {SizeText(entry.TotalBytes)}";
        var body = string.Join("\n", files.Take(10).Select(f => TransferNames.Clean(f.Name))) + (files.Count > 10 ? $"\n… 외 {files.Count - 10}개" : "");
        try
        {
            await _history.AddAsync(new DeviceNotificationRecord($"transfer-{entry.State.Id}", entry.State.DeviceId, entry.State.DeviceName,
                entry.State.DeviceKind, TransferHistory.Received, title, body, entry.State.Ts, _time.GetUtcNow()), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep transfer {Id} in the notification center", entry.State.Id);
        }
    }

    internal static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private long FreeSpace()
    {
        try { return _freeSpace?.Invoke(_root) ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return 0; }
    }

    private void Remove(Entry entry)
    {
        _entries.TryRemove(entry.State.Id, out _);
        try { Directory.Delete(entry.Dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete transfer folder {Dir}: {Message}", entry.Dir, ex.Message);
        }
    }

    private static string FilePath(string dir, int index) => Path.Combine(dir, index.ToString(CultureInfo.InvariantCulture) + ".bin");

    private static void Save(Entry entry)
    {
        var path = Path.Combine(entry.Dir, "transfer.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entry.State, MobileProtocol.Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Transfers kept across a restart; a folder without a readable state is deleted.</summary>
    private void Load()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            TransferState? state = null;
            try
            {
                var path = Path.Combine(dir, "transfer.json");
                if (File.Exists(path)) state = JsonSerializer.Deserialize<TransferState>(File.ReadAllText(path), MobileProtocol.Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
            if (state is { Id.Length: > 0 } && ClipProtocol.IsValidId(state.Id) && Path.GetFileName(dir) == state.Id
                && state.Have.Count == state.Files.Count)
            {
                _entries[state.Id] = new Entry(state, dir);
                continue;
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static TransferReply Ok<T>(T value) => new(200, JsonSerializer.Serialize(value, MobileProtocol.Json));

    private static TransferReply Unknown() => Error(409, TransferErrors.Unknown, "No such transfer here (finished, cancelled or dropped).");

    private static TransferReply Error(int status, string code, string message) =>
        new(status, JsonSerializer.Serialize(new ErrorResponse(MobileProtocol.Schema, code, message), MobileProtocol.Json));
}

/// <summary>
/// Finished files a phone uploaded for other phones of this PC (M-67): where they are, their checked hashes, the phones
/// (device ids) and who sent them.
/// </summary>
public sealed record ForwardedFiles(string TransferId, IReadOnlyList<TransferFile> Files, IReadOnlyList<string> Paths,
    IReadOnlyList<string> Sha256, IReadOnlyList<string> To, string FromName, string FromDeviceId);
