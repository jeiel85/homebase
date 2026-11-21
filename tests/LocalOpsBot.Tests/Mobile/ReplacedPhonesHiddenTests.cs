using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>
/// The old entry a reinstalled phone left behind stays out of the lists people see (DECISIONS M-72): the group's device
/// list, the phones a phone can send to through this PC, and the Tray's send targets — until it reports again.
/// </summary>
public class ReplacedPhonesHiddenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 36, 0, TimeSpan.FromHours(9));

    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly InMemoryClipKeys _keys = new();

    /// <summary>The case from 2026-11-21: the GitHub build's S24 went quiet, the Play build paired the day after.</summary>
    private (MobileDeviceInfo Old, MobileDeviceInfo Fresh, MobileDeviceInfo Tab) Reinstalled(DateTimeOffset? oldSeen = null)
    {
        var old = _store.AddAt("용은의 S24", "SM-S921N", "h1", T0, oldSeen ?? T0.AddDays(5.65));
        var fresh = _store.AddAt("용은의 S24", "SM-S921N", "h2", T0.AddDays(6.7), T0.AddDays(6.8));
        var tab = _store.AddAt("Galaxy Tab S9", "SM-X710", "h3", T0.AddDays(1), T0.AddDays(6.8));
        foreach (var d in new[] { old, fresh, tab })
        {
            _store.SetPushSubscription(d.DeviceId, new PushSubscription("https://ntfy.sh/up" + d.DeviceId, "k", "a", T0,
                Features: [ClipProtocol.Feature, FileSendProtocol.Feature]));
            _keys.Set(ClipTargetKeys.Device(d.DeviceId), new byte[32]);
        }
        return (old, fresh, tab);
    }

    private DeviceHub Hub() => new(_store, new DeviceStatusBoard(_time), new NotificationInbox(_time),
        () => new DeviceStatus("0.59.0"), null, "HUB", "0.59.0", NullLogger.Instance);

    private ClipShareService Clips() =>
        new(_store, _keys, (_, _, _) => Task.FromResult((true, (string?)null)), null, _time, NullLogger.Instance);

    private FileSendService Sends(string dir) =>
        new(dir, _store, _keys, _time, NullLogger.Instance, (_, _, _) => Task.FromResult((true, (string?)null)),
            freeSpace: _ => long.MaxValue / 2);

    [Fact]
    public void The_groups_device_list_leaves_the_old_entry_out()
    {
        var (old, fresh, tab) = Reinstalled();

        var ids = Hub().Devices().Devices.Select(d => d.DeviceId).ToList();

        Assert.DoesNotContain(old.DeviceId, ids);
        Assert.Contains(fresh.DeviceId, ids);
        Assert.Contains(tab.DeviceId, ids);
        Assert.Equal(3, _store.List().Count); // hidden, not removed
    }

    [Fact]
    public void An_entry_that_reported_after_the_new_pairing_is_another_phone_and_stays_listed()
    {
        var (old, _, _) = Reinstalled(oldSeen: T0.AddDays(6.75));

        Assert.Contains(old.DeviceId, Hub().Devices().Devices.Select(d => d.DeviceId));
    }

    [Fact]
    public void Phones_and_the_tray_send_to_the_phone_that_connected_again_not_its_old_entry()
    {
        var (old, fresh, tab) = Reinstalled();
        var clips = Clips();

        var relay = clips.RelayPhones(tab).Phones.Select(p => p.DeviceId).ToList();
        Assert.Equal(new[] { fresh.DeviceId }, relay);

        var clipTargets = clips.Targets().Select(t => t.Key).ToList();
        Assert.DoesNotContain(ClipTargetKeys.Device(old.DeviceId), clipTargets);
        Assert.Contains(ClipTargetKeys.Device(fresh.DeviceId), clipTargets);

        var dir = Path.Combine(Path.GetTempPath(), "hb-replaced-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fileTargets = Sends(dir).Targets().Select(t => t.Key).ToList();
            Assert.DoesNotContain(ClipTargetKeys.Device(old.DeviceId), fileTargets);
            Assert.Contains(ClipTargetKeys.Device(fresh.DeviceId), fileTargets);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
