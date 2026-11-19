using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services.Mobile;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// Settings › Phone's PC list: a group links PCs both ways (M-47), so the same PC is both a hub this PC
/// linked to and a device paired here. It must show once, on the PC list, never among the phones.
/// </summary>
public sealed class LinkedPcsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static LinkedHubView Hub(string pcId, string name, bool online = false, DateTimeOffset? contact = null,
        DeviceStatus? status = null, string? error = null, params DeviceEntry[] devices) =>
        new(pcId, name, online, contact, status, devices, error);

    private static DeviceEntry Pc(string deviceId, string name, string? pcId, bool online = false, DateTimeOffset? seen = null,
        DeviceStatus? status = null) =>
        new(deviceId, name, DeviceKind.Pc, null, seen, online, status, pcId);

    private static DeviceEntry Phone(string deviceId, string name) =>
        new(deviceId, name, DeviceKind.Phone, "SM-S921N", Now, true, null);

    [Fact]
    public void A_pc_linked_both_ways_is_one_row()
    {
        var rows = LinkedPcs.Merge(
            new[] { Hub("pc-loq", "DESKTOP-V0JCEPJ") },
            new[] { Phone("p1", "S24"), Pc("d1", "DESKTOP-V0JCEPJ", "pc-loq", online: true, seen: Now) });

        var row = Assert.Single(rows);
        Assert.Equal("pc-loq", row.PcId);
        Assert.True(row.Hub);
        // This PC's link is still connecting, but that PC reports here: it is online.
        Assert.True(row.Online);
        Assert.Equal(Now, row.LastSeen);
        // Unlinking by pcId reaches the pairing here too.
        Assert.Empty(row.RevokeIds);
    }

    [Fact]
    public void Phones_paired_here_are_not_pcs()
    {
        Assert.Empty(LinkedPcs.Merge(Array.Empty<LinkedHubView>(), new[] { Phone("p1", "S24") }));
    }

    [Fact]
    public void A_pc_whose_id_is_not_known_yet_joins_the_hub_of_the_same_name_and_is_revoked_by_device()
    {
        var rows = LinkedPcs.Merge(
            new[] { Hub("pc-loq", "DESKTOP-V0JCEPJ") },
            new[] { Pc("d1", " desktop-v0jcepj ", pcId: null, seen: Now) });

        var row = Assert.Single(rows);
        Assert.Equal("pc-loq", row.PcId);
        Assert.Equal(new[] { "d1" }, row.RevokeIds);
    }

    [Fact]
    public void A_known_pc_id_never_joins_another_pc_of_the_same_name()
    {
        var rows = LinkedPcs.Merge(
            new[] { Hub("pc-a", "OFFICE") },
            new[] { Pc("d1", "OFFICE", "pc-b") });

        Assert.Equal(new[] { "pc-a", "pc-b" }, rows.Select(r => r.PcId));
        Assert.False(rows[1].Hub);
    }

    [Fact]
    public void A_pc_only_paired_here_has_its_own_row()
    {
        var rows = LinkedPcs.Merge(Array.Empty<LinkedHubView>(), new[] { Pc("d1", "LAPTOP", pcId: null, seen: Now) });

        var row = Assert.Single(rows);
        Assert.Null(row.PcId);
        Assert.False(row.Hub);
        Assert.Equal(new[] { "d1" }, row.RevokeIds);
    }

    [Fact]
    public void The_reporting_half_gives_the_state()
    {
        var stale = new DeviceStatus(CpuPct: 90);
        var fresh = new DeviceStatus(CpuPct: 5);

        var hubDown = Assert.Single(LinkedPcs.Merge(
            new[] { Hub("pc-a", "A", online: false, status: stale) },
            new[] { Pc("d1", "A", "pc-a", online: true, status: fresh) }));
        Assert.Equal(fresh, hubDown.Status);

        var hubUp = Assert.Single(LinkedPcs.Merge(
            new[] { Hub("pc-a", "A", online: true, status: fresh) },
            new[] { Pc("d1", "A", "pc-a", online: true, status: stale) }));
        Assert.Equal(fresh, hubUp.Status);
    }

    [Fact]
    public void The_hub_counts_its_phones()
    {
        var row = Assert.Single(LinkedPcs.Merge(
            new[] { Hub("pc-a", "A", devices: new[] { Phone("p1", "S24"), Pc("self", "HERE", "pc-here") }) },
            Array.Empty<DeviceEntry>()));
        Assert.Equal(1, row.Phones);
    }
}
