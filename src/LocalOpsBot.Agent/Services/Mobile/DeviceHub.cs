using System.Collections.Concurrent;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// This PC as a hub (DECISIONS M-27, M-28): the state its paired devices report, the device list it
/// hands back, the notifications they send, and the inbox linked PCs poll. Since v0.34.0 (M-46) it also
/// asks a linked PC to share its live snapshots while this PC's Tray is looking at it, and keeps the
/// latest one: the linked PC holds the token, so this side cannot open its live view itself.
/// </summary>
public sealed class DeviceHub
{
    /// <summary>How long one inbox poll may wait for something new.</summary>
    public static readonly TimeSpan InboxWait = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Inbox polls that may wait at the same time. Each linked PC holds one for up to
    /// <see cref="InboxWait"/>, and they share the mobile API's 32 connections with phones; past this,
    /// a poll with nothing to return is turned away at once so phone requests keep getting through.
    /// </summary>
    public const int MaxInboxWaiters = 16;

    /// <summary>How long one <see cref="WantLive"/> keeps a linked PC sharing; the Tray renews it every second or two.</summary>
    public static readonly TimeSpan LiveLease = TimeSpan.FromSeconds(15);

    private int _inboxWaiters;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _liveWanted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (LiveSnapshot Snapshot, DateTimeOffset At)> _liveFrames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _liveNudges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _livePending = new(StringComparer.Ordinal);
    private readonly IMobileDeviceStore _devices;
    private readonly DeviceStatusBoard _board;
    private readonly NotificationInbox _inbox;
    private readonly Func<DeviceStatus> _selfStatus;
    private readonly Func<ToastOrigin, DeviceNotification, Task>? _relay;
    private readonly string _pcName;
    private readonly string _agentVersion;
    private readonly ILogger _logger;

    public DeviceHub(
        IMobileDeviceStore devices,
        DeviceStatusBoard board,
        NotificationInbox inbox,
        Func<DeviceStatus> selfStatus,
        Func<ToastOrigin, DeviceNotification, Task>? relay,
        string pcName,
        string agentVersion,
        ILogger logger,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _devices = devices;
        _board = board;
        _inbox = inbox;
        _selfStatus = selfStatus;
        _relay = relay;
        _pcName = pcName;
        _agentVersion = agentVersion;
        _logger = logger;
    }

    /// <summary>Whether device notifications can be relayed (Windows notification forwarding is on).</summary>
    public bool CanRelay => _relay is not null;

    public NotificationInbox Inbox => _inbox;

    /// <summary>The UnifiedPush endpoints this PC delivers to, hashed (M-28).</summary>
    public IReadOnlyList<string> OwnPushHashes() =>
        _devices.PushTargets().Select(t => PushHash.Of(t.Subscription.Endpoint)).Distinct().ToList();

    public DevicesResponse Devices()
    {
        var devices = _devices.List();
        // A device revoked in the Tray (or replaced by a re-pair) leaves its last report behind until here.
        _board.Retain(devices.Select(d => d.DeviceId));
        foreach (var gone in _liveFrames.Keys.Where(id => devices.All(d => d.DeviceId != id)).ToList())
            _liveFrames.TryRemove(gone, out _);
        return new(
            MobileProtocol.Schema,
            new HubEntry(_devices.PcId, _pcName, _agentVersion, _selfStatus()),
            _board.Entries(devices),
            OwnPushHashes() is { Count: > 0 } hashes ? hashes : null);
    }

    public DevicesResponse Presence(MobileDeviceInfo device, PresenceReport report)
    {
        _board.Report(device.DeviceId, report.Sanitized());
        return Devices();
    }

    /// <summary>
    /// What the Tray's device page adds for devices paired here (M-46): pairing date and whether each one
    /// takes push from this PC (and which kinds it turned off).
    /// </summary>
    public IReadOnlyList<LocalDeviceInfo> LocalInfo()
    {
        var subs = new Dictionary<string, PushSubscription>(StringComparer.Ordinal);
        foreach (var t in _devices.PushTargets()) subs[t.DeviceId] = t.Subscription;
        return _devices.List().Select(d => new LocalDeviceInfo(d.DeviceId, d.PairedAt,
            subs.ContainsKey(d.DeviceId), subs.TryGetValue(d.DeviceId, out var sub) ? sub.MutedCategories : null)).ToList();
    }

    /// <summary>
    /// The Tray is looking at the PC paired here as <paramref name="deviceId"/> (M-46): ask it, through its
    /// inbox poll, to share its live snapshots for <see cref="LiveLease"/>. Renewing an active request does
    /// not wake the poll again; the inbox answers carry <c>live: true</c> for as long as the lease runs.
    /// </summary>
    public void WantLive(string deviceId)
    {
        var now = _time.GetUtcNow();
        var rising = !IsLiveWanted(deviceId, now);
        _liveWanted[deviceId] = now + LiveLease;
        if (!rising) return;
        // Wake the poll that is waiting now; with none waiting, answer the next one at once.
        if (_liveNudges.TryRemove(deviceId, out var nudge)) nudge.TrySetResult();
        else _livePending[deviceId] = true;
    }

    public bool IsLiveWanted(string deviceId) => IsLiveWanted(deviceId, _time.GetUtcNow());

    private bool IsLiveWanted(string deviceId, DateTimeOffset now) =>
        _liveWanted.TryGetValue(deviceId, out var until) && until > now;

    /// <summary>
    /// A shared live snapshot from a linked PC (<c>POST /api/v1/live-snapshot</c>). Kept only while it is
    /// wanted; the answer tells the sender whether to go on.
    /// </summary>
    public bool AcceptLive(MobileDeviceInfo device, LiveSnapshot snapshot)
    {
        if (!IsLiveWanted(device.DeviceId))
        {
            _liveFrames.TryRemove(device.DeviceId, out _);
            return false;
        }
        _liveFrames[device.DeviceId] = (snapshot, _time.GetUtcNow());
        return true;
    }

    /// <summary>The latest snapshot <paramref name="deviceId"/> shared, and when it arrived.</summary>
    public (LiveSnapshot Snapshot, DateTimeOffset At)? LiveFrame(string deviceId) =>
        _liveFrames.TryGetValue(deviceId, out var f) ? f : null;

    public async Task RelayAsync(MobileDeviceInfo device, DeviceNotification n)
    {
        if (_relay is null) return;
        var origin = new ToastOrigin(device.DeviceId, device.Name, FromHub: false, _board.PushHashesOf(device.DeviceId), device.Kind);
        _logger.LogInformation("Notification from {Device} ({App}) received for relay", device.Name, n.App);
        await _relay(origin, n); // n.Id travels along so a PC reached by two paths shows it once
    }

    /// <summary>
    /// One inbox poll: what is new after <paramref name="after"/>, waiting up to <see cref="InboxWait"/>
    /// for it. Returns <c>null</c> without waiting when <see cref="MaxInboxWaiters"/> polls already wait
    /// and there is nothing to return; the caller answers that as busy and the linked PC retries later.
    /// </summary>
    public async Task<InboxResponse?> InboxAsync(MobileDeviceInfo device, long after, CancellationToken ct)
    {
        var id = device.DeviceId;
        // A live request that came while this PC was between polls: answer at once (M-46).
        if (_livePending.TryRemove(id, out _) && IsLiveWanted(id)) return WithLive(_inbox.Read(after, id), id);

        if (Interlocked.Increment(ref _inboxWaiters) > MaxInboxWaiters)
        {
            Interlocked.Decrement(ref _inboxWaiters);
            var now = _inbox.Read(after, id);
            // Something new, or a cursor from before a hub restart: answer as a waiting poll would.
            return now.Items.Count > 0 || after > now.Next ? WithLive(now, id) : null;
        }
        var nudge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _liveNudges[id] = nudge;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var wait = _inbox.WaitAsync(after, id, InboxWait, stop.Token);
            if (await Task.WhenAny(wait, nudge.Task) == wait) return WithLive(await wait, id);
            stop.Cancel();
            try { await wait; } catch (OperationCanceledException) { }
            return WithLive(_inbox.Read(after, id), id);
        }
        finally
        {
            _liveNudges.TryRemove(new KeyValuePair<string, TaskCompletionSource>(id, nudge));
            Interlocked.Decrement(ref _inboxWaiters);
        }
    }

    private InboxResponse WithLive(InboxResponse r, string deviceId) => IsLiveWanted(deviceId) ? r with { Live = true } : r;

    /// <summary>The device unpaired itself (<c>DELETE /api/v1/device</c>).</summary>
    public bool Unpair(MobileDeviceInfo device)
    {
        _board.Forget(device.DeviceId);
        _liveFrames.TryRemove(device.DeviceId, out _);
        _liveWanted.TryRemove(device.DeviceId, out _);
        var removed = _devices.Revoke(device.DeviceId);
        if (removed) _logger.LogInformation("{Name} ({DeviceId}) unpaired itself.", device.Name, device.DeviceId);
        return removed;
    }
}
