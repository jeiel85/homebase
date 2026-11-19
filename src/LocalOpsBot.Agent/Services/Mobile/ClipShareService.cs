using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>What <see cref="ClipShareService.Accept"/> did with text another device sent.</summary>
public enum ClipAcceptOutcome
{
    /// <summary>A Tray has it (or had it already: the same message again).</summary>
    Accepted,
    /// <summary>Not ours, not decryptable with that device's key, or out of bounds.</summary>
    Invalid,
    /// <summary>No Tray that takes clipboard text is connected: nobody is signed in to this PC.</summary>
    NoUserSession,
}

/// <summary>
/// Clipboard text sent by hand between paired devices (DECISIONS M-62, <c>docs/CLIPBOARD_SHARE.md</c> phase 1).
/// <list type="bullet">
/// <item><b>In</b>: <c>POST /api/v1/clip</c> from a phone or a linked PC → the Tray puts it on the clipboard. The
/// Agent (LocalSystem, session 0) never touches a clipboard itself.</item>
/// <item><b>Out</b>, the Tray's "send clipboard": to a phone as a push sealed with that phone's key — text too long
/// for a push stays here for <see cref="ClipProtocol.OfferLifetime"/> and the phone fetches it — and to a linked PC
/// with <c>POST /api/v1/clip</c> over the link.</item>
/// </list>
/// Nothing here watches the clipboard, and the text is never logged or stored on disk.
/// </summary>
public sealed class ClipShareService
{
    /// <summary>Sends one plaintext push to one phone (the UnifiedPush channel; a fake in tests).</summary>
    public delegate Task<(bool Ok, string? Error)> PushSender(PushTarget target, byte[] plaintext, CancellationToken ct);

    /// <summary>How long a message id is remembered, so a retry over another path is not pasted twice.</summary>
    internal static readonly TimeSpan SeenFor = TimeSpan.FromMinutes(30);
    private const int MaxSeen = 512;
    private const int MaxOffers = 16;
    /// <summary>Per path of the other PC (an unreachable one usually fails sooner, at the 5 s connect timeout).</summary>
    private static readonly TimeSpan PcTimeout = TimeSpan.FromSeconds(8);

    private readonly IMobileDeviceStore _devices;
    private readonly PushSender? _push;
    private readonly HubLinkService? _hubs;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Offer> _offers = new(StringComparer.Ordinal);

    private sealed record Offer(string Text, bool Sensitive, DateTimeOffset Ts, HashSet<string> DeviceIds, DateTimeOffset Expires);

    public ClipShareService(IMobileDeviceStore devices, PushSender? push, HubLinkService? hubs, TimeProvider time, ILogger logger)
    {
        _devices = devices;
        _push = push;
        _hubs = hubs;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Hands text to the connected Trays (set by the mobile bridge). Returns whether one took it; false (or unset)
    /// means nobody is signed in, and the sender is told so.
    /// </summary>
    public Func<TrayClip, bool>? Tray { get; set; }

    /// <summary>
    /// Text a paired device sent (<c>POST /api/v1/clip</c>). <paramref name="token"/> is the bearer token it
    /// authenticated with: its hash is the key the text is sealed with.
    /// </summary>
    public ClipAcceptOutcome Accept(MobileDeviceInfo device, string token, ClipEnvelope? envelope)
    {
        var content = ClipMessages.Open(PairingService.HashToken(token), envelope);
        if (content?.Text is not { } text) return ClipAcceptOutcome.Invalid;

        var key = device.DeviceId + ":" + envelope!.Id;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (_seen.ContainsKey(key)) return ClipAcceptOutcome.Accepted;
        }

        var sensitive = content.Sensitive == true;
        if (Tray?.Invoke(new TrayClip(text, device.Name, device.Kind, sensitive)) != true)
        {
            _logger.LogInformation("Clipboard text from {Name} not taken: no Tray is connected (nobody signed in).", device.Name);
            return ClipAcceptOutcome.NoUserSession;
        }
        lock (_gate)
        {
            _seen[key] = now;
            if (_seen.Count > MaxSeen)
                foreach (var old in _seen.OrderBy(p => p.Value).Take(_seen.Count - MaxSeen).Select(p => p.Key).ToList()) _seen.Remove(old);
        }
        _logger.LogInformation("Clipboard text from {Name} ({Chars} chars{Sensitive}) handed to the Tray.",
            device.Name, text.Length, sensitive ? ", sensitive" : "");
        return ClipAcceptOutcome.Accepted;
    }

    /// <summary>
    /// Text too long for a push that this PC offered to <paramref name="device"/> (<c>GET /api/v1/clip?id=</c>),
    /// sealed for it; null when it was not offered to that device or has expired.
    /// </summary>
    public ClipEnvelope? Fetch(MobileDeviceInfo device, string token, string? id)
    {
        if (!ClipProtocol.IsValidId(id)) return null;
        Offer? offer;
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            if (!_offers.TryGetValue(id!, out offer) || !offer.DeviceIds.Contains(device.DeviceId)) return null;
        }
        _logger.LogInformation("Long clipboard text {Id} fetched by {Name}.", id, device.Name);
        return ClipMessages.Seal(PairingService.HashToken(token), id!,
            new ClipContent(MobileProtocol.Schema, offer.Text, offer.Text.Length, offer.Ts, offer.Sensitive ? true : null));
    }

    /// <summary>
    /// Where the Tray can send: phones paired here that take clipboard pushes (an older app would drop them
    /// silently), then the PCs this one is linked to, each by name.
    /// </summary>
    public IReadOnlyList<ClipTarget> Targets()
    {
        var phones = _push is null
            ? Enumerable.Empty<ClipTarget>()
            : _devices.PushTargets()
                .Where(t => t.Subscription.Supports(ClipProtocol.Feature) && !string.IsNullOrEmpty(t.TokenSha256))
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
        return new ClipSendResult(outcomes);
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
        if (_push is null || target is null || string.IsNullOrEmpty(target.TokenSha256) || !target.Subscription.Supports(ClipProtocol.Feature))
            return new ClipSendOutcome(key, target?.DeviceName ?? key, false, ClipErrors.UnknownTarget);

        var plaintext = ClipMessages.PushPlaintext(_devices.PcId, target.TokenSha256, id, content);
        if (plaintext is null)
        {
            // Too long for one push: the push says how long, and the phone fetches the text from here.
            Remember(id, content, deviceId);
            plaintext = ClipMessages.PushPlaintext(_devices.PcId, target.TokenSha256, id, content with { Text = null })
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
        var envelope = ClipMessages.Seal(PairingService.HashToken(hub.Token), id, content);
        var reply = await _hubs!.CallAsync(pcId, HttpMethod.Post, ClipProtocol.Path, envelope, PcTimeout, ct);
        var error = reply switch
        {
            null => ClipErrors.UnknownTarget,
            { Ok: true } => null,
            { Status: 0 } => ClipErrors.Unreachable,
            { Status: 404 } => ClipErrors.Unsupported, // an Agent from before M-62
            { Status: 401 or 403 } => ClipErrors.Rejected,
            { Status: 409 } => ClipErrors.NoUserSession,
            { Status: 400 } => ClipErrors.Invalid,
            _ => ClipErrors.Failed,
        };
        if (error is not null)
            _logger.LogWarning("Clipboard text to {Pc} failed: {Error} (HTTP {Status}{Detail})", hub.PcName, error,
                reply?.Status ?? 0, reply?.Error is { } e ? ", " + e : "");
        return new ClipSendOutcome(key, hub.PcName, error is null, error);
    }

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
                new HashSet<string>(StringComparer.Ordinal) { deviceId }, now + ClipProtocol.OfferLifetime);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var gone in _offers.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _offers.Remove(gone);
        foreach (var old in _seen.Where(p => now - p.Value > SeenFor).Select(p => p.Key).ToList()) _seen.Remove(old);
    }
}
