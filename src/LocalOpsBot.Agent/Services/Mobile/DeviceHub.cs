using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// This PC as a hub (DECISIONS M-27, M-28): the state its paired devices report, the device list it
/// hands back, the notifications they send, and the inbox linked PCs poll.
/// </summary>
public sealed class DeviceHub
{
    /// <summary>How long one inbox poll may wait for something new.</summary>
    public static readonly TimeSpan InboxWait = TimeSpan.FromSeconds(25);

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
        ILogger logger)
    {
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

    public DevicesResponse Devices() => new(
        MobileProtocol.Schema,
        new HubEntry(_devices.PcId, _pcName, _agentVersion, _selfStatus()),
        _board.Entries(_devices.List()),
        OwnPushHashes() is { Count: > 0 } hashes ? hashes : null);

    public DevicesResponse Presence(MobileDeviceInfo device, PresenceReport report)
    {
        _board.Report(device.DeviceId, report);
        return Devices();
    }

    public async Task RelayAsync(MobileDeviceInfo device, DeviceNotification n)
    {
        if (_relay is null) return;
        var origin = new ToastOrigin(device.DeviceId, device.Name, FromHub: false, _board.PushHashesOf(device.DeviceId));
        _logger.LogInformation("Notification from {Device} ({App}) received for relay", device.Name, n.App);
        await _relay(origin, n); // n.Id travels along so a PC reached by two paths shows it once
    }

    public Task<InboxResponse> InboxAsync(MobileDeviceInfo device, long after, CancellationToken ct) =>
        _inbox.WaitAsync(after, device.DeviceId, InboxWait, ct);

    /// <summary>The device unpaired itself (<c>DELETE /api/v1/device</c>).</summary>
    public bool Unpair(MobileDeviceInfo device)
    {
        _board.Forget(device.DeviceId);
        var removed = _devices.Revoke(device.DeviceId);
        if (removed) _logger.LogInformation("{Name} ({DeviceId}) unpaired itself.", device.Name, device.DeviceId);
        return removed;
    }
}
