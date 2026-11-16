using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Hardening #37: hub inbox long polls are capped, and revoked devices leave no report behind.</summary>
public class DeviceHubLimitsTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly DeviceStatusBoard _board;
    private readonly NotificationInbox _inbox;
    private readonly DeviceHub _hub;

    public DeviceHubLimitsTests()
    {
        _board = new DeviceStatusBoard(_time);
        _inbox = new NotificationInbox(_time);
        _hub = new DeviceHub(_store, _board, _inbox, () => new DeviceStatus("0.30.0"), null, "HUB", "0.30.0", NullLogger.Instance);
    }

    private void AddToInbox(string title) =>
        _inbox.Add(NotificationInbox.SelfOrigin, "HUB", "Slack", title, null, _time.GetUtcNow());

    [Fact]
    public async Task Inbox_polls_past_the_limit_are_turned_away_instead_of_holding_a_connection()
    {
        var pc = _store.Add("WORK-PC", "Windows", "h", DeviceKind.Pc);
        AddToInbox("first");
        var latest = _inbox.LatestSeq;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var waiting = Enumerable.Range(0, DeviceHub.MaxInboxWaiters).Select(_ => _hub.InboxAsync(pc, latest, cts.Token)).ToList();
        Assert.All(waiting, t => Assert.False(t.IsCompleted));

        Assert.Null(await _hub.InboxAsync(pc, latest, cts.Token)); // nothing new: busy, at once
        Assert.Single((await _hub.InboxAsync(pc, 0, cts.Token))!.Items); // something new is still answered

        AddToInbox("second");
        foreach (var t in waiting) Assert.Equal("second", Assert.Single((await t)!.Items).Title);

        // The finished polls freed their slots: the next one waits again.
        var next = _hub.InboxAsync(pc, _inbox.LatestSeq, cts.Token);
        Assert.False(next.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public void A_revoked_device_loses_its_last_report()
    {
        var phone = _store.Add("Galaxy", "SM-S921N", "h1");
        var tablet = _store.Add("Tab", "SM-X710", "h2");
        _hub.Presence(phone, new PresenceReport(1, DeviceKind.Phone, "0.30.0", Battery: 50));
        _hub.Presence(tablet, new PresenceReport(1, DeviceKind.Phone, "0.30.0", Battery: 70));
        Assert.Equal(2, _board.Count);

        _store.Revoke(phone.DeviceId); // what the Tray's revoke does

        var devices = _hub.Devices();
        Assert.Equal(1, _board.Count);
        Assert.Equal("Tab", Assert.Single(devices.Devices).Name);
    }
}
