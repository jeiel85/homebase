using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>What <see cref="ClipShareService.AcceptAsync"/> did with text another device sent.</summary>
public enum ClipAcceptOutcome
{
    /// <summary>A Tray put it on the clipboard (or had already: the same message again).</summary>
    Accepted,
    /// <summary>Not ours, not decryptable with the key agreed with that device, or out of bounds.</summary>
    Invalid,
    /// <summary>No key agreed with that device yet (or this PC lost it): agree one and send again.</summary>
    KeyNeeded,
    /// <summary>No Tray that takes clipboard text is connected: nobody is signed in to this PC.</summary>
    NoUserSession,
    /// <summary>A Tray got it but could not put it on the clipboard (busy), or did not answer in time.</summary>
    ClipboardBusy,
}

/// <summary>
/// Clipboard text sent by hand between paired devices (DECISIONS M-62, <c>docs/CLIPBOARD_SHARE.md</c> phase 1).
/// <list type="bullet">
/// <item><b>Keys</b>: each device agrees one with this PC by ECDH (<c>POST /api/v1/clip/key</c>); this PC agrees
/// one with each PC it is linked to before it first sends there. The bearer token plays no part, so the Cloudflare
/// edge, which sees it, still can't read the text.</item>
/// <item><b>In</b>: <c>POST /api/v1/clip</c> from a phone or a linked PC → the Tray puts it on the clipboard and says
/// whether it did. The Agent (LocalSystem, session 0) never touches a clipboard itself.</item>
/// <item><b>Out</b>, the Tray's "send clipboard": to a phone as a push sealed with that phone's key — text too long
/// for a push stays here for <see cref="ClipProtocol.OfferLifetime"/> and the phone fetches it — and to a linked PC
/// with <c>POST /api/v1/clip</c> over the link.</item>
/// </list>
/// Nothing here watches the clipboard, and the text is never logged. What came in and went out is kept in the notification
/// center (M-70, <see cref="ClipHistory"/>) — the text too, unless it was marked sensitive.
/// </summary>
public sealed class ClipShareService
{
    /// <summary>Sends one plaintext push to one phone (the UnifiedPush channel; a fake in tests).</summary>
    public delegate Task<(bool Ok, string? Error)> PushSender(PushTarget target, byte[] plaintext, CancellationToken ct);

    /// <summary>
    /// Hands text to the connected Trays and waits for their answer: null — no Tray takes clipboard text (nobody
    /// signed in); true — one put it on the clipboard; false — none could, or none answered in time.
    /// </summary>
    public delegate Task<bool?> TrayDelivery(TrayClip clip, CancellationToken ct);

    /// <summary>How long a message id is remembered, so a retry over another path is not pasted twice.</summary>
    internal static readonly TimeSpan SeenFor = TimeSpan.FromMinutes(30);
    private const int MaxSeen = 512;
    private const int MaxOffers = 16;
    /// <summary>Per path of the other PC (an unreachable one usually fails sooner, at the 5 s connect timeout).</summary>
    private static readonly TimeSpan PcTimeout = TimeSpan.FromSeconds(8);

    private readonly IMobileDeviceStore _devices;
    private readonly IClipKeyStore _keys;
    private readonly PushSender? _push;
    private readonly HubLinkService? _hubs;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly INotificationHistoryStore? _history;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Offer> _offers = new(StringComparer.Ordinal);

    // From: the phone that sent it through this PC (M-67), handed back with the text like the push carries it.
    private sealed record Offer(string Text, bool Sensitive, DateTimeOffset Ts, HashSet<string> DeviceIds, DateTimeOffset Expires,
        string? From = null);

    public ClipShareService(IMobileDeviceStore devices, IClipKeyStore keys, PushSender? push, HubLinkService? hubs,
        TimeProvider time, ILogger logger, INotificationHistoryStore? history = null)
    {
        _devices = devices;
        _history = history;
        _keys = keys;
        _push = push;
        _hubs = hubs;
        _time = time;
        _logger = logger;
    }

    /// <summary>The connected Trays (set by the mobile bridge); unset means nobody is signed in.</summary>
    public TrayDelivery? Tray { get; set; }

    /// <summary>
    /// A paired device's half of the key exchange (<c>POST /api/v1/clip/key</c>): stores the agreed key for it,
    /// replacing any earlier one, and returns this PC's half. Null when the device's public key is not valid.
    /// </summary>
    public ClipKeyExchange? AgreeKey(MobileDeviceInfo device, ClipKeyExchange? request)
    {
        if (ClipKeys.Answer(request) is not { } agreed) return null;
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), agreed.Key);
        _logger.LogInformation("Clipboard key agreed with {Name} ({DeviceId}).", device.Name, device.DeviceId);
        return agreed.Answer;
    }

    /// <summary>Text a paired device sent (<c>POST /api/v1/clip</c>), sealed with the key agreed with it.</summary>
    public async Task<ClipAcceptOutcome> AcceptAsync(MobileDeviceInfo device, ClipEnvelope? envelope, CancellationToken ct) =>
        (await ReceiveAsync(device, envelope, ct)).Outcome;

    /// <summary>
    /// Text a paired device sent (<c>POST /api/v1/clip</c>): onto this PC's clipboard, or — with <see cref="ClipContent.To"/>
    /// (M-67) — on to those phones instead, sealed with each one's key and carrying the sender's name. Then
    /// <c>Forwarded</c> has one outcome per phone asked.
    /// </summary>
    public async Task<(ClipAcceptOutcome Outcome, IReadOnlyList<ClipForwardOutcome>? Forwarded)> ReceiveAsync(MobileDeviceInfo device,
        ClipEnvelope? envelope, CancellationToken ct)
    {
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return (ClipAcceptOutcome.KeyNeeded, null);
        var content = ClipMessages.Open(key, envelope);
        if (content?.Text is not { } text) return (ClipAcceptOutcome.Invalid, null);

        var seenKey = device.DeviceId + ":" + envelope!.Id;
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            if (_seen.ContainsKey(seenKey)) return (ClipAcceptOutcome.Accepted, content.To is null ? null : Array.Empty<ClipForwardOutcome>());
        }

        if (content.To is { Count: > 0 } to)
        {
            var forwarded = await ForwardAsync(device, content, to, ct);
            // Remembered only when it reached someone: a sender trying again after a total failure tries again here too.
            if (forwarded.Any(f => f.Ok)) Seen(seenKey);
            return (ClipAcceptOutcome.Accepted, forwarded);
        }

        var sensitive = content.Sensitive == true;
        var written = Tray is { } tray ? await tray(new TrayClip(text, device.Name, device.Kind, sensitive), ct) : null;
        if (written is null)
        {
            _logger.LogInformation("Clipboard text from {Name} not taken: no Tray is connected (nobody signed in).", device.Name);
            return (ClipAcceptOutcome.NoUserSession, null);
        }
        if (written == false)
        {
            // Not remembered: the sender may try the same message again.
            _logger.LogWarning("Clipboard text from {Name} could not be put on the clipboard (busy or no answer).", device.Name);
            return (ClipAcceptOutcome.ClipboardBusy, null);
        }
        Seen(seenKey);
        _logger.LogInformation("Clipboard text from {Name} ({Chars} chars{Sensitive}) put on the clipboard.",
            device.Name, text.Length, sensitive ? ", sensitive" : "");
        await RecordAsync(new DeviceNotificationRecord($"clip-in-{envelope.Id}", device.DeviceId, device.Name, device.Kind,
            ClipHistory.Received, ClipHistory.Title(text.Length, sensitive), ClipHistory.Body(text, sensitive), content.Ts, _time.GetUtcNow()));
        return (ClipAcceptOutcome.Accepted, null);
    }

    private void Seen(string seenKey)
    {
        lock (_gate)
        {
            _seen[seenKey] = _time.GetUtcNow();
            if (_seen.Count > MaxSeen)
                foreach (var old in _seen.OrderBy(p => p.Value).Take(_seen.Count - MaxSeen).Select(p => p.Key).ToList()) _seen.Remove(old);
        }
    }

    /// <summary>Text a phone sent for other phones of this PC (M-67): this PC's own push to each, with the sender's name.</summary>
    private async Task<IReadOnlyList<ClipForwardOutcome>> ForwardAsync(MobileDeviceInfo from, ClipContent content, IReadOnlyList<string> to,
        CancellationToken ct)
    {
        var passed = content with { To = null, From = from.Name };
        var id = ClipProtocol.NewId();
        var outcomes = await Task.WhenAll(to.Distinct(StringComparer.Ordinal).Select(async deviceId =>
        {
            if (deviceId == from.DeviceId) return new ClipForwardOutcome(deviceId, false, ClipErrors.UnknownTarget);
            try
            {
                var sent = await SendToPhoneAsync(ClipTargetKeys.Device(deviceId), deviceId, id, passed, ct);
                return new ClipForwardOutcome(deviceId, sent.Ok, sent.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Passing clipboard text from {Name} on to {DeviceId} failed", from.Name, deviceId);
                return new ClipForwardOutcome(deviceId, false, ClipErrors.Failed);
            }
        }));
        _logger.LogInformation("Clipboard text from {Name} ({Chars} chars) passed on: {Outcomes}.", from.Name, content.Chars,
            string.Join(", ", outcomes.Select(o => o.Ok ? $"{o.DeviceId} ok" : $"{o.DeviceId} {o.Error}")));
        return outcomes;
    }

    /// <summary>
    /// <c>GET /api/v1/share/phones</c> (M-67): the other phones paired here that agreed a key with this PC, and whether each
    /// takes clipboard text and files — what a phone may ask this PC to pass on.
    /// </summary>
    public SharePhones RelayPhones(MobileDeviceInfo asking)
    {
        if (_push is null) return new SharePhones(MobileProtocol.Schema, Array.Empty<SharePhone>());
        var replaced = PhoneReinstalls.Superseded(_devices);
        var phones = _devices.PushTargets()
            .Where(t => t.DeviceId != asking.DeviceId && !replaced.Contains(t.DeviceId) && _keys.Get(ClipTargetKeys.Device(t.DeviceId)) is not null)
            .GroupBy(t => t.DeviceId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(t => new SharePhone(t.DeviceId, t.DeviceName, t.Subscription.Supports(ClipProtocol.Feature),
                t.Subscription.Supports(FileSendProtocol.Feature)))
            .Where(p => p.Clip || p.Files)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new SharePhones(MobileProtocol.Schema, phones);
    }

    /// <summary>
    /// Text too long for a push that this PC offered to <paramref name="device"/> (<c>GET /api/v1/clip?id=</c>),
    /// sealed with its key; null when it was not offered to that device or has expired.
    /// </summary>
    public ClipEnvelope? Fetch(MobileDeviceInfo device, string? id)
    {
        if (!ClipProtocol.IsValidId(id)) return null;
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return null;
        Offer? offer;
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            if (!_offers.TryGetValue(id!, out offer) || !offer.DeviceIds.Contains(device.DeviceId)) return null;
        }
        _logger.LogInformation("Long clipboard text {Id} fetched by {Name}.", id, device.Name);
        return ClipMessages.Seal(key, id!,
            new ClipContent(MobileProtocol.Schema, offer.Text, offer.Text.Length, offer.Ts, offer.Sensitive ? true : null, From: offer.From));
    }

    /// <summary>
    /// Where the Tray can send: phones paired here that take clipboard pushes (an older app would drop them
    /// silently) and have agreed a key with this PC, then the PCs this one is linked to, each by name.
    /// </summary>
    public IReadOnlyList<ClipTarget> Targets()
    {
        var replaced = PhoneReinstalls.Superseded(_devices);
        var phones = _push is null
            ? Enumerable.Empty<ClipTarget>()
            : _devices.PushTargets()
                .Where(t => t.Subscription.Supports(ClipProtocol.Feature) && !replaced.Contains(t.DeviceId) &&
                            _keys.Get(ClipTargetKeys.Device(t.DeviceId)) is not null)
                .Select(t => new ClipTarget(ClipTargetKeys.Device(t.DeviceId), t.DeviceName, DeviceKind.Phone))
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase);
        var pcs = (_hubs?.Hubs() ?? Array.Empty<LinkedHub>())
            .Select(h => new ClipTarget(ClipTargetKeys.Pc(h.PcId), h.PcName, DeviceKind.Pc))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase);
        return phones.Concat(pcs).ToList();
    }

    /// <summary>The Tray's "send clipboard": every target at once, one outcome each.</summary>
    public async Task<ClipSendResult> SendAsync(ClipSendRequest request, CancellationToken ct)
    {
        var keys = (request.Targets ?? Array.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        var text = request.Text ?? "";
        if (text.Length == 0 || text.Length > ClipProtocol.MaxChars)
            return new ClipSendResult(keys.Select(k => new ClipSendOutcome(k, k, false, ClipErrors.Invalid)).ToList());

        var id = ClipProtocol.NewId();
        var content = new ClipContent(MobileProtocol.Schema, text, text.Length, _time.GetUtcNow(), request.Sensitive ? true : null);
        var outcomes = await Task.WhenAll(keys.Select(k => SendOneAsync(k, id, content, ct)));
        _logger.LogInformation("Clipboard text ({Chars} chars{Sensitive}) sent from the Tray: {Outcomes}.", text.Length,
            request.Sensitive ? ", sensitive" : "",
            string.Join(", ", outcomes.Select(o => o.Ok ? $"{o.Name} ok" : $"{o.Name} {o.Error}")));
        // One entry per device, like files sent (M-66); a target no longer paired or linked has no name to show.
        foreach (var o in outcomes.Where(o => o.Error != ClipErrors.UnknownTarget))
        {
            var kind = ClipTargetKeys.PcId(o.Key) is not null ? DeviceKind.Pc : DeviceKind.Phone;
            await RecordAsync(new DeviceNotificationRecord($"clip-out-{id}-{o.Key}", ClipTargetKeys.DeviceId(o.Key) ?? ClipTargetKeys.PcId(o.Key) ?? o.Key,
                o.Name, kind, ClipHistory.Sent, ClipHistory.Title(text.Length, request.Sensitive, ClipHistory.Outcome(o)),
                ClipHistory.Body(text, request.Sensitive), content.Ts, _time.GetUtcNow()));
        }
        return new ClipSendResult(outcomes);
    }

    /// <summary>Keeps one entry in the notification center (M-70); a failure there never fails the clipboard itself.</summary>
    private async Task RecordAsync(DeviceNotificationRecord record)
    {
        if (_history is null) return;
        try
        {
            await _history.AddAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep clipboard text in the notification center");
        }
    }

    private async Task<ClipSendOutcome> SendOneAsync(string key, string id, ClipContent content, CancellationToken ct)
    {
        try
        {
            if (ClipTargetKeys.DeviceId(key) is { } deviceId) return await SendToPhoneAsync(key, deviceId, id, content, ct);
            if (ClipTargetKeys.PcId(key) is { } pcId) return await SendToPcAsync(key, pcId, id, content, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sending clipboard text to {Target} failed", key);
            return new ClipSendOutcome(key, key, false, ClipErrors.Failed);
        }
        return new ClipSendOutcome(key, key, false, ClipErrors.UnknownTarget);
    }

    private async Task<ClipSendOutcome> SendToPhoneAsync(string key, string deviceId, string id, ClipContent content, CancellationToken ct)
    {
        var target = _devices.PushTargets().FirstOrDefault(t => t.DeviceId == deviceId);
        var clipKey = _keys.Get(ClipTargetKeys.Device(deviceId));
        if (_push is null || target is null || clipKey is null || !target.Subscription.Supports(ClipProtocol.Feature))
            return new ClipSendOutcome(key, target?.DeviceName ?? key, false, ClipErrors.UnknownTarget);

        var plaintext = ClipMessages.PushPlaintext(_devices.PcId, clipKey, id, content);
        if (plaintext is null)
        {
            // Too long for one push: the push says how long, and the phone fetches the text from here.
            Remember(id, content, deviceId);
            plaintext = ClipMessages.PushPlaintext(_devices.PcId, clipKey, id, content with { Text = null })
                ?? throw new InvalidOperationException("A clipboard push without text does not fit in one push.");
        }
        var (ok, error) = await _push(target, plaintext, ct);
        if (!ok) _logger.LogWarning("Clipboard push to {Device} failed: {Error}", target.DeviceName, error);
        return new ClipSendOutcome(key, target.DeviceName, ok, ok ? null : ClipErrors.PushFailed);
    }

    private async Task<ClipSendOutcome> SendToPcAsync(string key, string pcId, string id, ClipContent content, CancellationToken ct)
    {
        var hub = _hubs?.Hub(pcId);
        if (hub is null) return new ClipSendOutcome(key, key, false, ClipErrors.UnknownTarget);

        byte[]? stale = null;
        string? error = null;
        int status = 0;
        // Twice at most: a stored key the other PC no longer knows (it was set up again) is agreed afresh once.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (clipKey, keyError, keyStatus) = await KeyForPcAsync(pcId, stale, ct);
            if (clipKey is null)
            {
                (error, status) = (keyError, keyStatus);
                break;
            }
            var reply = await _hubs!.CallAsync(pcId, HttpMethod.Post, ClipProtocol.Path, ClipMessages.Seal(clipKey, id, content), PcTimeout, ct);
            status = reply?.Status ?? 0;
            error = ErrorOf(reply);
            if (error is ClipErrors.KeyNeeded or ClipErrors.Invalid && attempt == 0)
            {
                stale = clipKey;
                continue;
            }
            break;
        }
        if (error is not null)
            _logger.LogWarning("Clipboard text to {Pc} failed: {Error} (HTTP {Status})", hub.PcName, error, status);
        return new ClipSendOutcome(key, hub.PcName, error is null, error);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _agreeing = new(StringComparer.Ordinal);

    /// <summary>
    /// The clipboard key agreed with linked PC <paramref name="pcId"/> (M-62), for clipboard text and files (M-65) alike:
    /// the stored one unless it is <paramref name="stale"/> (that PC no longer knows it), else a newly agreed one. One
    /// exchange per PC at a time — the other PC keeps the key of the exchange it answered last, so two at once could
    /// leave this PC holding the other one; a flow that waited finds the key the first one agreed.
    /// </summary>
    public async Task<(byte[]? Key, string? Error, int Status)> KeyForPcAsync(string pcId, byte[]? stale, CancellationToken ct)
    {
        var name = ClipTargetKeys.Pc(pcId);
        var gate = _agreeing.GetOrAdd(pcId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_keys.Get(name) is { } stored && (stale is null || !stored.AsSpan().SequenceEqual(stale))) return (stored, null, 200);
            var (key, error, status) = await AgreeWithPcAsync(pcId, ct);
            if (key is not null) _keys.Set(name, key);
            return (key, error, status);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>This PC's side of the exchange with a PC it is linked to: the key, or why there is none.</summary>
    private async Task<(byte[]? Key, string? Error, int Status)> AgreeWithPcAsync(string pcId, CancellationToken ct)
    {
        var (own, request) = ClipKeys.Begin();
        using (own)
        {
            var reply = await _hubs!.CallAsync(pcId, HttpMethod.Post, ClipProtocol.KeyPath, request, PcTimeout, ct);
            if (reply is not { Ok: true }) return (null, ErrorOf(reply) ?? ClipErrors.Failed, reply?.Status ?? 0);
            var key = ClipKeys.Finish(own, reply.Read<ClipKeyExchange>());
            if (key is null) return (null, ClipErrors.Failed, reply.Status);
            _logger.LogInformation("Clipboard key agreed with linked PC {PcId}.", pcId);
            return (key, null, reply.Status);
        }
    }

    /// <summary>The <see cref="ClipErrors"/> code for a linked PC's answer; null when it took the text.</summary>
    internal static string? ErrorOf(HubReply? reply) => reply switch
    {
        null => ClipErrors.UnknownTarget,
        { Ok: true } => null,
        { Status: 0 } => ClipErrors.Unreachable,
        { Status: 404 } => ClipErrors.Unsupported, // an Agent from before M-62
        { Status: 401 or 403 } => ClipErrors.Rejected,
        { Status: 409 } => reply.ErrorCode == ClipErrors.KeyNeeded ? ClipErrors.KeyNeeded : ClipErrors.NoUserSession,
        { Status: 503 } when reply.ErrorCode == ClipErrors.ClipboardBusy => ClipErrors.ClipboardBusy,
        { Status: 400 } => ClipErrors.Invalid,
        _ => ClipErrors.Failed,
    };

    private void Remember(string id, ClipContent content, string deviceId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (_offers.TryGetValue(id, out var existing))
            {
                existing.DeviceIds.Add(deviceId);
                return;
            }
            if (_offers.Count >= MaxOffers)
                _offers.Remove(_offers.OrderBy(p => p.Value.Expires).First().Key);
            _offers[id] = new Offer(content.Text!, content.Sensitive == true, content.Ts,
                new HashSet<string>(StringComparer.Ordinal) { deviceId }, now + ClipProtocol.OfferLifetime, content.From);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var gone in _offers.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _offers.Remove(gone);
        foreach (var old in _seen.Where(p => now - p.Value > SeenFor).Select(p => p.Key).ToList()) _seen.Remove(old);
    }
}
