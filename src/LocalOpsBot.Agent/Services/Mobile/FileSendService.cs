using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>A fetched chunk (binary) or an error answer (JSON).</summary>
public readonly record struct FetchReply(int Status, byte[]? Bytes, string? Json);

/// <summary>
/// Files this PC sends (DECISIONS M-65, <c>docs/FILE_SHARE.md</c> 2b). The Tray reads them with the user's rights and
/// streams them over <see cref="TransferPipe"/> (mode <c>write</c>) into <c>ProgramData\Homebase\mobile\outgoing\&lt;id&gt;</c>
/// (SYSTEM and Administrators only); from there:
/// <list type="bullet">
/// <item><b>Phones</b> get a <c>share</c> push and fetch the chunks with <c>GET /api/v1/transfer/fetch</c>, each sealed under
/// that phone's key, then say <c>received</c>.</item>
/// <item><b>PCs</b> get them uploaded by this Agent with their 2a routes (M-64), as a device of them.</item>
/// </list>
/// The Tray hears how each target stands (<c>files.progress</c>). A send is dropped once every target is done, or after
/// <see cref="TransferProtocol.KeepFor"/>.
/// </summary>
public sealed class FileSendService : BackgroundService
{
    internal const long StoreCapBytes = 10L * 1024 * 1024 * 1024;
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PcTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(2);
    private const int MaxBusyRetries = 60;
    private const int MaxNetworkRetries = 3;
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(1);

    private readonly string _root;
    private readonly long _storeCap;
    private readonly LocalOpsBot.Core.Notifications.INotificationHistoryStore? _history;
    private readonly IMobileDeviceStore _devices;
    private readonly IClipKeyStore _keys;
    private readonly ClipShareService.PushSender? _push;
    private readonly HubLinkService? _hubs;
    private readonly ClipShareService? _clips;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<string, long>? _freeSpace;
    private readonly ConcurrentDictionary<string, Send> _sends = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationToken _stopping = CancellationToken.None;

    /// <summary>The state kept in <c>send.json</c>.</summary>
    internal sealed class SendState
    {
        public string Id { get; set; } = "";
        public List<TransferFile> Files { get; set; } = new();
        /// <summary>Per file, lowercase hex SHA-256 once the Tray has written it; null before.</summary>
        public List<string?> Sha256 { get; set; } = new();
        public List<TargetState> Targets { get; set; } = new();
        public bool Started { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        /// <summary>The phone they came from, when this PC passes another phone's files on (M-67); null for this PC's own.</summary>
        public string? From { get; set; }
    }

    internal sealed class TargetState
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string State { get; set; } = FileSendStates.Sending;
        public long Sent { get; set; }
        public string? Error { get; set; }
    }

    private sealed class Send(SendState state, string dir)
    {
        public SendState State { get; } = state;
        public string Dir { get; } = dir;
        public object Gate { get; } = new();
        public long TotalBytes => State.Files.Sum(f => f.Size);

        /// <summary>Bytes the Tray has yet to write for a send that hasn't started (a file counts until it is whole).</summary>
        public long OutstandingBytes => State.Started ? 0 : State.Files.Where((_, i) => State.Sha256[i] is null).Sum(f => f.Size);

        public bool Final(TargetState t) => t.State is FileSendStates.Sent or FileSendStates.Received or FileSendStates.Failed;
    }

    public FileSendService(string root, IMobileDeviceStore devices, IClipKeyStore keys, TimeProvider time, ILogger logger,
        ClipShareService.PushSender? push = null, HubLinkService? hubs = null, ClipShareService? clips = null, Func<string, long>? freeSpace = null,
        LocalOpsBot.Core.Notifications.INotificationHistoryStore? history = null, long storeCapBytes = StoreCapBytes)
    {
        _storeCap = storeCapBytes;
        _history = history;
        _root = root;
        _devices = devices;
        _keys = keys;
        _time = time;
        _logger = logger;
        _push = push;
        _hubs = hubs;
        _clips = clips;
        _freeSpace = freeSpace;
        Directory.CreateDirectory(_root);
        Load();
    }

    /// <summary>
    /// Files a phone uploaded to this PC for other phones (M-67, <see cref="FileShareService.Forwarder"/>): moved (same disk)
    /// into a send of their own, already written and started, and announced to those phones like any send from here — with
    /// the sender's name. Phones not (or no longer) able to fetch files from here are left out. Null when on their way, else
    /// why not.
    /// </summary>
    public string? Adopt(ForwardedFiles files)
    {
        var known = Targets().ToDictionary(t => t.Key, StringComparer.Ordinal);
        var targets = files.To.Distinct(StringComparer.Ordinal)
            .Where(id => id != files.FromDeviceId)
            .Select(id => known.GetValueOrDefault(ClipTargetKeys.Device(id)))
            .OfType<ClipTarget>()
            .ToList();
        if (targets.Count == 0) return ClipErrors.UnknownTarget;
        if (files.Sha256.Count != files.Files.Count || files.Paths.Count != files.Files.Count) return TransferErrors.Invalid;

        Send send;
        lock (_gate)
        {
            if (_sends.Values.Sum(s => s.TotalBytes) + files.Files.Sum(f => f.Size) > _storeCap) return TransferErrors.NoSpace;
            var id = ClipProtocol.NewId();
            var state = new SendState
            {
                Id = id, Files = files.Files.ToList(), Sha256 = files.Sha256.Select(h => (string?)h).ToList(), Started = true,
                CreatedAt = _time.GetUtcNow(), From = files.FromName,
                Targets = targets.Select(t => new TargetState { Key = t.Key, Name = t.Name, Kind = t.Kind }).ToList(),
            };
            var dir = Path.Combine(_root, id);
            Directory.CreateDirectory(dir);
            try
            {
                for (var i = 0; i < files.Paths.Count; i++) File.Move(files.Paths[i], FilePath(dir, i));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Files from {Name} could not be taken for passing on: {Message}", files.FromName, ex.Message);
                try { Directory.Delete(dir, recursive: true); } catch (Exception del) when (del is IOException or UnauthorizedAccessException) { }
                return ClipErrors.Failed;
            }
            send = new Send(state, dir);
            Save(send);
            _sends[id] = send;
        }
        _logger.LogInformation("Send {Id}: {Count} file(s) from {Name} passed on to {Targets}.", send.State.Id, files.Files.Count,
            files.FromName, string.Join(", ", targets.Select(t => t.Name)));
        foreach (var target in send.State.Targets) _ = Task.Run(() => DeliverAsync(send, target, _stopping), CancellationToken.None);
        return null;
    }

    /// <summary>Progress for the Tray (the bridge broadcasts it as <c>files.progress</c>).</summary>
    public event Action<FilesProgress>? Progress;

    // ── Tray (bridge and the write mode of Homebase.Transfer) ─────────────────

    /// <summary>
    /// Where files can go: phones paired here that fetch files (<c>features: share</c>) and agreed a key, then the PCs this
    /// one is linked to.
    /// </summary>
    public IReadOnlyList<ClipTarget> Targets()
    {
        // Not the old entry a reinstalled phone left behind (M-72): its endpoint no longer reaches an app.
        var replaced = PhoneReinstalls.Superseded(_devices);
        var phones = _push is null
            ? Enumerable.Empty<ClipTarget>()
            : _devices.PushTargets()
                .Where(t => t.Subscription.Supports(FileSendProtocol.Feature) && !replaced.Contains(t.DeviceId) &&
                            _keys.Get(ClipTargetKeys.Device(t.DeviceId)) is not null)
                .Select(t => new ClipTarget(ClipTargetKeys.Device(t.DeviceId), t.DeviceName, DeviceKind.Phone))
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase);
        var pcs = (_hubs?.Hubs() ?? Array.Empty<LinkedHub>())
            .Select(h => new ClipTarget(ClipTargetKeys.Pc(h.PcId), h.PcName, DeviceKind.Pc))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase);
        return phones.Concat(pcs).ToList();
    }

    /// <summary><c>files.begin</c>: room for these files, an id to write them under.</summary>
    public FilesBeginResult Begin(FilesBeginRequest? request)
    {
        if (request is not { Targets.Count: > 0, Files: { } files }) return new(null, TransferErrors.Invalid);
        if (TransferProtocol.Check(new TransferMeta(MobileProtocol.Schema, files, _time.GetUtcNow())) is { } problem) return new(null, problem);
        var known = Targets().ToDictionary(t => t.Key, StringComparer.Ordinal);
        var targets = request.Targets.Distinct(StringComparer.Ordinal).Select(k => known.TryGetValue(k, out var t) ? t : null).ToList();
        if (targets.Any(t => t is null)) return new(null, TransferErrors.Invalid);

        var total = files.Sum(f => f.Size);
        lock (_gate)
        {
            var inUse = _sends.Values.Sum(s => s.TotalBytes);
            // Sends still being written from the Tray will take what they haven't written yet (Codex review on #100).
            var free = FreeSpace() - _sends.Values.Sum(s => s.OutstandingBytes);
            if (inUse + total > _storeCap || free < total + FileShareService.FreeMarginBytes)
                return new(null, TransferErrors.NoSpace);
            var id = ClipProtocol.NewId();
            var state = new SendState
            {
                Id = id, Files = files.ToList(), Sha256 = files.Select(_ => (string?)null).ToList(), CreatedAt = _time.GetUtcNow(),
                Targets = targets.Select(t => new TargetState { Key = t!.Key, Name = t.Name, Kind = t.Kind }).ToList(),
            };
            var dir = Path.Combine(_root, id);
            Directory.CreateDirectory(dir);
            var send = new Send(state, dir);
            Save(send);
            _sends[id] = send;
            _logger.LogInformation("Send {Id}: {Count} file(s), {Total} bytes, to {Targets}.", id, files.Count, total,
                string.Join(", ", state.Targets.Select(t => t.Name)));
            return new(id, null);
        }
    }

    /// <summary>
    /// The pipe's write mode: the file to write <paramref name="request"/>'s bytes to, when the send exists, hasn't
    /// started and the size is the one declared. The caller writes exactly that many bytes, then calls <see cref="Written"/>.
    /// </summary>
    public FileStream? OpenForWrite(TransferPipeRequest? request)
    {
        if (request is not { Mode: TransferPipeRequest.Write } || !_sends.TryGetValue(request.Id, out var send)) return null;
        lock (send.Gate)
        {
            if (send.State.Started || request.File < 0 || request.File >= send.State.Files.Count
                || send.State.Files[request.File].Size != request.Size)
                return null;
            send.State.Sha256[request.File] = null;
            return new FileStream(FilePath(send.Dir, request.File), FileMode.Create, FileAccess.Write, FileShare.None);
        }
    }

    public void Written(string id, int file, string sha256)
    {
        if (!_sends.TryGetValue(id, out var send)) return;
        lock (send.Gate)
        {
            send.State.Sha256[file] = sha256;
            Save(send);
        }
    }

    /// <summary><c>files.go</c>: every file is in — send them. False when one is missing.</summary>
    public bool Go(string? id)
    {
        if (id is null || !_sends.TryGetValue(id, out var send)) return false;
        lock (send.Gate)
        {
            if (send.State.Started) return true;
            if (send.State.Sha256.Any(h => h is null)) return false;
            send.State.Started = true;
            Save(send);
        }
        foreach (var target in send.State.Targets) _ = Task.Run(() => DeliverAsync(send, target, _stopping), CancellationToken.None);
        return true;
    }

    /// <summary><c>files.cancel</c>: the Tray gave up (or the user cancelled before it went out).</summary>
    public bool Cancel(string? id)
    {
        if (id is null || !_sends.TryGetValue(id, out var send)) return false;
        Remove(send);
        _logger.LogInformation("Send {Id} cancelled from the Tray.", id);
        return true;
    }

    // ── Phones (API) ───────────────────────────────────────────────────────────

    /// <summary><c>GET /api/v1/transfer/offer?id=</c>: what this PC sends the asking phone, sealed for it.</summary>
    public TransferReply Offer(MobileDeviceInfo device, string? id)
    {
        if (Find(device, id) is not { } found) return Unknown();
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return Error(409, ClipErrors.KeyNeeded, "Agree a clipboard key first.");
        var offer = new TransferOffer(MobileProtocol.Schema,
            found.Send.State.Files.Select((f, i) => new TransferOfferFile(f.Name, f.Size, f.Mime, found.Send.State.Sha256[i]!)).ToList(),
            found.Send.State.CreatedAt);
        return new TransferReply(200, JsonSerializer.Serialize(FileSendMessages.SealOffer(key, found.Send.State.Id, offer), MobileProtocol.Json));
    }

    /// <summary><c>GET /api/v1/transfer/fetch?id=&amp;f=&amp;n=</c>: one chunk, sealed under the asking phone's key.</summary>
    public async Task<FetchReply> FetchAsync(MobileDeviceInfo device, string? id, string? f, string? n, CancellationToken ct)
    {
        if (Find(device, id) is not { } found) return Json(Unknown());
        if (!int.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var file) || file >= found.Send.State.Files.Count
            || !long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var chunk))
            return Json(Error(400, TransferErrors.BadChunk, "No such chunk."));
        var length = TransferProtocol.ChunkLength(found.Send.State.Files[file].Size, chunk);
        if (length == 0) return Json(Error(400, TransferErrors.BadChunk, "No such chunk."));
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return Json(Error(409, ClipErrors.KeyNeeded, "Agree a clipboard key first."));

        var plain = new byte[length];
        await using (var stream = new FileStream(FilePath(found.Send.Dir, file), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Position = chunk * TransferProtocol.ChunkBytes;
            await stream.ReadExactlyAsync(plain, ct);
        }
        var target = found.Target;
        lock (found.Send.Gate) target.Sent = Math.Max(target.Sent, chunk * TransferProtocol.ChunkBytes + length + Offset(found.Send, file));
        Report(found.Send, target, throttle: true);
        return new FetchReply(200, TransferCrypto.SealChunk(TransferCrypto.TransferKey(key, found.Send.State.Id), file, chunk, plain), null);
    }

    /// <summary><c>POST /api/v1/transfer/received?id=</c>: the phone has every file.</summary>
    public TransferReply Received(MobileDeviceInfo device, string? id)
    {
        if (Find(device, id) is not { } found) return Unknown();
        Finish(found.Send, found.Target, FileSendStates.Received, null);
        _logger.LogInformation("Send {Id}: {Name} received the files.", found.Send.State.Id, device.Name);
        return new TransferReply(200, "{\"schema\":1,\"ok\":true}");
    }

    // ── Delivery ───────────────────────────────────────────────────────────────

    private async Task DeliverAsync(Send send, TargetState target, CancellationToken ct)
    {
        try
        {
            if (ClipTargetKeys.DeviceId(target.Key) is { } deviceId) await NotifyPhoneAsync(send, target, deviceId, ct);
            else if (ClipTargetKeys.PcId(target.Key) is { } pcId) await UploadToPcAsync(send, target, pcId, ct);
            else Finish(send, target, FileSendStates.Failed, ClipErrors.UnknownTarget);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send {Id} to {Name} failed", send.State.Id, target.Name);
            Finish(send, target, FileSendStates.Failed, ClipErrors.Failed);
        }
    }

    private async Task NotifyPhoneAsync(Send send, TargetState target, string deviceId, CancellationToken ct)
    {
        var push = _devices.PushTargets().FirstOrDefault(t => t.DeviceId == deviceId);
        if (_push is null || push is null || !push.Subscription.Supports(FileSendProtocol.Feature)
            || _keys.Get(ClipTargetKeys.Device(deviceId)) is not { } key)
        {
            Finish(send, target, FileSendStates.Failed, ClipErrors.UnknownTarget);
            return;
        }
        var notice = new ShareNotice(MobileProtocol.Schema, send.State.Files.Count, send.TotalBytes, send.State.Files[0].Name, send.State.CreatedAt,
            send.State.From);
        var (ok, error) = await _push(push, FileSendMessages.PushPlaintext(_devices.PcId, key, send.State.Id, notice), ct);
        if (!ok)
        {
            _logger.LogWarning("Send {Id}: push to {Name} failed: {Error}", send.State.Id, target.Name, error);
            Finish(send, target, FileSendStates.Failed, ClipErrors.PushFailed);
            return;
        }
        lock (send.Gate)
        {
            target.State = FileSendStates.Notified;
            Save(send);
        }
        Report(send, target);
    }

    /// <summary>Uploads to a linked PC with its 2a routes, going on from its status after a break.</summary>
    private async Task UploadToPcAsync(Send send, TargetState target, string pcId, CancellationToken ct)
    {
        if (_hubs is null || _clips is null || _hubs.Hub(pcId) is null)
        {
            Finish(send, target, FileSendStates.Failed, ClipErrors.UnknownTarget);
            return;
        }
        var id = send.State.Id;
        byte[]? stale = null;
        for (var round = 0; round < 3; round++)
        {
            var (key, keyError, _) = await _clips.KeyForPcAsync(pcId, stale, ct);
            if (key is null)
            {
                Finish(send, target, FileSendStates.Failed, keyError ?? ClipErrors.Failed);
                return;
            }
            var meta = new TransferMeta(MobileProtocol.Schema, send.State.Files, send.State.CreatedAt);
            var created = await CallWithRetryAsync(() => _hubs.CallAsync(pcId, HttpMethod.Post, TransferProtocol.Path,
                TransferMessages.SealMeta(key, id, meta), PcTimeout, ct), ct);
            if (created is { Status: 409 } && created.ErrorCode == ClipErrors.KeyNeeded || created is { Status: 400 } && round == 0)
            {
                stale = key; // that PC was set up again: agree afresh once
                continue;
            }
            if (created is not { Ok: true } || created.Read<TransferStatus>() is not { } status)
            {
                Finish(send, target, FileSendStates.Failed, PcError(created));
                return;
            }

            var transferKey = TransferCrypto.TransferKey(key, id);
            var lost = false;
            for (var f = 0; f < send.State.Files.Count && !lost; f++)
            {
                var have = ChunkRanges.Parse(f < status.Have.Count ? status.Have[f] : "")?.ToHashSet() ?? new HashSet<long>();
                var size = send.State.Files[f].Size;
                await using var stream = new FileStream(FilePath(send.Dir, f), FileMode.Open, FileAccess.Read, FileShare.Read);
                for (long n = 0; n < TransferProtocol.ChunkCount(size); n++)
                {
                    var length = TransferProtocol.ChunkLength(size, n);
                    if (!have.Contains(n))
                    {
                        var plain = new byte[length];
                        stream.Position = n * TransferProtocol.ChunkBytes;
                        await stream.ReadExactlyAsync(plain, ct);
                        var sealedChunk = TransferCrypto.SealChunk(transferKey, f, n, plain);
                        var reply = await CallWithRetryAsync(() => _hubs.CallBytesAsync(pcId,
                            $"{TransferProtocol.ChunkPath}?id={id}&f={f}&n={n}", sealedChunk, PcTimeout, ct), ct);
                        if (reply is { Status: 409 } && reply.ErrorCode == TransferErrors.Unknown)
                        {
                            lost = true; // the other PC dropped it (restart of its store, 24 h): start over from its status
                            break;
                        }
                        if (reply is not { Ok: true })
                        {
                            Finish(send, target, FileSendStates.Failed, PcError(reply));
                            return;
                        }
                    }
                    lock (send.Gate) target.Sent = Offset(send, f) + n * TransferProtocol.ChunkBytes + length;
                    Report(send, target, throttle: true);
                }
            }
            if (lost) continue;

            var hashes = new TransferHashes(MobileProtocol.Schema, send.State.Sha256.Select(h => h!).ToList());
            var done = await CallWithRetryAsync(() => _hubs.CallAsync(pcId, HttpMethod.Post, $"{TransferProtocol.DonePath}?id={id}",
                TransferMessages.SealHashes(key, id, hashes), PcTimeout, ct), ct);
            if (done is { Status: 409 } && done.ErrorCode is TransferErrors.Incomplete or TransferErrors.Unknown) continue;
            if (done is not { Ok: true })
            {
                Finish(send, target, FileSendStates.Failed, PcError(done));
                return;
            }
            Finish(send, target, FileSendStates.Sent, null);
            _logger.LogInformation("Send {Id}: {Name} has the files ({Saved}).", id, target.Name, done.Read<TransferDone>()?.Saved ?? "?");
            return;
        }
        Finish(send, target, FileSendStates.Failed, ClipErrors.Failed);
    }

    /// <summary>A call to another PC, again after "busy" (its chunk turns are taken) and after a short network break.</summary>
    private static async Task<HubReply?> CallWithRetryAsync(Func<Task<HubReply?>> call, CancellationToken ct)
    {
        HubReply? reply = null;
        for (int busy = 0, network = 0; ;)
        {
            reply = await call();
            if (reply is { Status: 503 } && reply.ErrorCode == TransferErrors.Busy && busy++ < MaxBusyRetries)
            {
                await Task.Delay(BusyWait, ct);
                continue;
            }
            if (reply is { Status: 0 } && network++ < MaxNetworkRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * network), ct);
                continue;
            }
            return reply;
        }
    }

    private static string PcError(HubReply? reply) => reply switch
    {
        { Status: 413 } => TransferErrors.TooLarge,
        { Status: 507 } => TransferErrors.NoSpace,
        _ => ClipShareService.ErrorOf(reply) ?? ClipErrors.Failed,
    };

    private static long Offset(Send send, int file) => send.State.Files.Take(file).Sum(f => f.Size);

    private void Finish(Send send, TargetState target, string state, string? error)
    {
        bool allDone;
        lock (send.Gate)
        {
            target.State = state;
            target.Error = error;
            if (state != FileSendStates.Failed) target.Sent = send.TotalBytes;
            allDone = send.State.Targets.All(send.Final);
            if (!allDone) Save(send);
        }
        // Gone before the last progress goes out: whoever sees a send end finds nothing of it left on disk.
        if (allDone) Remove(send);
        Report(send, target);
        if (send.Final(target)) _ = RecordAsync(send, target);
    }

    /// <summary>
    /// What went to one device, once it is settled, in the notification center beside what came in (DECISIONS M-66):
    /// names, size and how it ended — never the files.
    /// </summary>
    private async Task RecordAsync(Send send, TargetState target)
    {
        if (_history is null) return;
        var files = send.State.Files;
        var title = $"파일 {files.Count}개 · {FileShareService.SizeText(send.TotalBytes)}";
        var outcome = target.State switch
        {
            FileSendStates.Sent => "보냄",
            FileSendStates.Received => "받음",
            _ => "실패: " + FailureText(target.Error),
        };
        if (send.State.From is { } from) outcome += $" · {from}에서 받아 넘김";
        var body = outcome + "\n" + string.Join("\n", files.Take(10).Select(f => TransferNames.Clean(f.Name)))
            + (files.Count > 10 ? $"\n… 외 {files.Count - 10}개" : "");
        var origin = ClipTargetKeys.DeviceId(target.Key) ?? ClipTargetKeys.PcId(target.Key) ?? target.Key;
        try
        {
            await _history.AddAsync(new LocalOpsBot.Core.Notifications.DeviceNotificationRecord($"send-{send.State.Id}-{target.Key}", origin,
                target.Name, target.Kind, TransferHistory.Sent, title, body, send.State.CreatedAt, _time.GetUtcNow()), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep send {Id} in the notification center", send.State.Id);
        }
    }

    /// <summary>A failure in words for the notification center (the Tray's balloons have their own, translated).</summary>
    internal static string FailureText(string? error) => error switch
    {
        "expired" => "24시간 안에 받아 가지 않음",
        ClipErrors.PushFailed => "휴대폰에 알리지 못함",
        TransferErrors.NoSpace => "받는 PC에 공간이 부족함",
        TransferErrors.TooLarge => "너무 큼",
        TransferErrors.HashMismatch => "받는 쪽에서 내용이 맞지 않음",
        null or "" => "알 수 없는 오류",
        var code => code,
    };

    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

    private void Report(Send send, TargetState target, bool throttle = false)
    {
        var now = _time.GetUtcNow();
        if (throttle)
        {
            lock (_gate)
            {
                if (now - _lastReport < ProgressEvery) return;
                _lastReport = now;
            }
        }
        FilesProgress progress;
        lock (send.Gate)
            progress = new FilesProgress(send.State.Id, target.Key, target.Name, target.Kind, target.State, target.Sent, send.TotalBytes, target.Error);
        try { Progress?.Invoke(progress); }
        catch (Exception ex) { _logger.LogDebug("Files progress to the Tray failed: {Message}", ex.Message); }
    }

    // ── Housekeeping ───────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        // Uploads to PCs that were on the way when the Agent stopped go on (from the other PC's status).
        foreach (var send in _sends.Values.Where(s => s.State.Started))
            foreach (var target in send.State.Targets.Where(t => t.State == FileSendStates.Sending && t.Key.StartsWith("pc:", StringComparison.Ordinal)))
                _ = Task.Run(() => DeliverAsync(send, target, stoppingToken), CancellationToken.None);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Sweep(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Sweeping old sends failed"); }
            try { await Task.Delay(SweepEvery, _time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Drops sends older than <see cref="TransferProtocol.KeepFor"/>; a phone that never fetched them counts as failed.</summary>
    internal void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var send in _sends.Values.ToList())
        {
            if (now - send.State.CreatedAt <= TransferProtocol.KeepFor) continue;
            var expired = send.State.Targets.Where(t => !send.Final(t)).ToList();
            foreach (var target in expired)
            {
                target.State = FileSendStates.Failed;
                target.Error = "expired";
                Report(send, target);
            }
            Remove(send);
            foreach (var target in expired) _ = RecordAsync(send, target);
            _logger.LogInformation("Send {Id} dropped after {Hours} h.", send.State.Id, (int)TransferProtocol.KeepFor.TotalHours);
        }
    }

    internal IReadOnlyList<string> Ids() => _sends.Keys.ToList();

    // ── Internals ──────────────────────────────────────────────────────────────

    private (Send Send, TargetState Target)? Find(MobileDeviceInfo device, string? id)
    {
        if (!ClipProtocol.IsValidId(id) || !_sends.TryGetValue(id!, out var send) || !send.State.Started) return null;
        var target = send.State.Targets.FirstOrDefault(t => t.Key == ClipTargetKeys.Device(device.DeviceId));
        return target is null || target.State == FileSendStates.Failed ? null : (send, target);
    }

    private long FreeSpace()
    {
        try { return _freeSpace?.Invoke(_root) ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return 0; }
    }

    private void Remove(Send send)
    {
        _sends.TryRemove(send.State.Id, out _);
        try { Directory.Delete(send.Dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete send folder {Dir}: {Message}", send.Dir, ex.Message);
        }
    }

    private static string FilePath(string dir, int index) => Path.Combine(dir, index.ToString(CultureInfo.InvariantCulture) + ".bin");

    private static void Save(Send send)
    {
        var path = Path.Combine(send.Dir, "send.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(send.State, MobileProtocol.Json));
        File.Move(tmp, path, overwrite: true);
    }

    private void Load()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            SendState? state = null;
            try
            {
                var path = Path.Combine(dir, "send.json");
                if (File.Exists(path)) state = JsonSerializer.Deserialize<SendState>(File.ReadAllText(path), MobileProtocol.Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
            // A send the Tray never finished writing can't go anywhere: dropped with the rest of the unreadable ones.
            if (state is { Started: true } && ClipProtocol.IsValidId(state.Id) && Path.GetFileName(dir) == state.Id
                && state.Sha256.Count == state.Files.Count && state.Sha256.All(h => h is not null))
            {
                _sends[state.Id] = new Send(state, dir);
                continue;
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static FetchReply Json(TransferReply reply) => new(reply.Status, null, reply.Json);

    private static TransferReply Unknown() => Error(409, TransferErrors.Unknown, "Nothing waits here for this device under that id.");

    private static TransferReply Error(int status, string code, string message) =>
        new(status, JsonSerializer.Serialize(new ErrorResponse(MobileProtocol.Schema, code, message), MobileProtocol.Json));
}
