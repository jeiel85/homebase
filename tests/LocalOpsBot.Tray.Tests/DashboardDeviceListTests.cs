using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Dashboard;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// The dashboard's device list: a PC linked both ways (M-47) is one entry, and its live view comes the way
/// that answers now — this PC's link while it is up, else that PC sharing its view here.
/// </summary>
public sealed class DashboardDeviceListTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static DeviceOverview Overview(IEnumerable<LinkedHubView> hubs, params DeviceEntry[] here) =>
        new(new DevicesResponse(1, new HubEntry("pc-self", "HERE", "0.42.2", new DeviceStatus()), here), hubs.ToList());

    private static LinkedHubView Hub(string pcId, string name, bool online, params DeviceEntry[] devices) =>
        new(pcId, name, online, online ? Now : null, online ? new DeviceStatus(CpuPct: 10) : null, devices, null);

    private static DeviceEntry Pc(string deviceId, string name, string? pcId, bool online) =>
        new(deviceId, name, DeviceKind.Pc, null, Now, online, online ? new DeviceStatus(CpuPct: 20) : null, pcId);

    private static DeviceEntry Phone(string deviceId, string name) =>
        new(deviceId, name, DeviceKind.Phone, "SM-S921N", Now, true, null);

    private static IReadOnlyList<DeviceItem> Others(DeviceOverview o) =>
        DeviceList.Build(o, "HERE").Where(i => i.Role != DeviceRole.Self).ToList();

    [Fact]
    public void A_pc_linked_both_ways_is_one_entry_viewed_through_this_pcs_link_while_it_is_up()
    {
        var items = Others(Overview(new[] { Hub("pc-loq", "DESKTOP-V0JCEPJ", online: true) },
            Pc("d1", "DESKTOP-V0JCEPJ", "pc-loq", online: true), Phone("p1", "S24")));

        var pc = Assert.Single(items, i => i.IsPc);
        Assert.Equal("hub:pc-loq", pc.Key);
        Assert.Equal(DeviceRole.Hub, pc.Role);
        Assert.Equal(LiveTarget.Hub("pc-loq"), pc.LiveTarget);
        Assert.True(pc.TwoWay);
        Assert.Single(items, i => !i.IsPc);
    }

    [Fact]
    public void While_this_pcs_link_does_not_get_through_the_pc_shares_its_view_here_under_the_same_key()
    {
        var items = Others(Overview(new[] { Hub("pc-loq", "DESKTOP-V0JCEPJ", online: false) },
            Pc("d1", "DESKTOP-V0JCEPJ", "pc-loq", online: true)));

        var pc = Assert.Single(items);
        Assert.Equal("hub:pc-loq", pc.Key);
        Assert.Equal(DeviceRole.LinkedHere, pc.Role);
        Assert.Equal(LiveTarget.Device("d1"), pc.LiveTarget);
        Assert.True(pc.Online);
        Assert.Equal(20, pc.Status?.CpuPct);
        Assert.Equal("pc:pc-loq", pc.AliasKey);
    }

    [Fact]
    public void Both_ways_down_it_waits_on_this_pcs_link()
    {
        var pc = Assert.Single(Others(Overview(new[] { Hub("pc-loq", "LOQ", online: false) }, Pc("d1", "LOQ", "pc-loq", online: false))));
        Assert.Equal(DeviceRole.Hub, pc.Role);
        Assert.False(pc.Online);
    }

    [Fact]
    public void One_way_links_keep_their_role()
    {
        var items = Others(Overview(new[] { Hub("pc-a", "A", online: true) }, Pc("d1", "B", "pc-b", online: true)));

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Key == "hub:pc-a" && i.Role == DeviceRole.Hub && !i.TwoWay);
        Assert.Contains(items, i => i.Key == "device:d1" && i.Role == DeviceRole.LinkedHere && !i.TwoWay);
    }

    [Fact]
    public void A_pc_seen_through_another_pc_is_not_listed_again()
    {
        var items = Others(Overview(
            new[] { Hub("pc-a", "A", online: true, Pc("x-b", "B", "pc-b", online: true), Pc("x-self", "HERE", "pc-self", online: true)) },
            Pc("d1", "A", "pc-a", online: true), Pc("d2", "B", "pc-b", online: true)));

        Assert.Equal(new[] { "A", "B" }, items.Select(i => i.Name).OrderBy(n => n));
        Assert.DoesNotContain(items, i => i.Role == DeviceRole.ViaHub);
    }
}
