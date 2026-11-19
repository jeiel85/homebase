using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// One PC linked to this one. In a group PCs connect both ways (DECISIONS M-47): <c>Hub</c> is this PC's
/// link to it (this PC holds its token), <c>Here</c> its pairings here as a device — usually one, none when
/// it only accepted this PC's link.
/// </summary>
internal sealed record PcLink(LinkedHubView? Hub, IReadOnlyList<DeviceEntry> Here)
{
    /// <summary>Its pcId: the hub's, else what it reported here (null until it reports after an Agent restart).</summary>
    public string? PcId => Hub?.PcId ?? Here.Select(d => d.PcId).FirstOrDefault(id => id is { Length: > 0 });

    public string Name => Hub?.PcName ?? Here[0].Name;
}

/// <summary>
/// One PC on Settings › Phone's PC list: both halves of a <see cref="PcLink"/> as one row.
/// <c>PcId</c> is null only for a PC paired here that has not reported its id since the Agent started.
/// <c>RevokeIds</c>: devices paired here that unlinking by <c>PcId</c> does not reach (their id is not
/// known yet), so they are revoked one by one.
/// </summary>
internal sealed record LinkedPc(
    string Name,
    string? PcId,
    bool Hub,
    bool Online,
    DateTimeOffset? LastSeen,
    DeviceStatus? Status,
    bool Rejected,
    int Phones,
    IReadOnlyList<string> RevokeIds);

internal static class LinkedPcs
{
    /// <summary>
    /// The PCs this one is linked to either way, once each, hubs first in the Agent's order. A PC paired here
    /// joins the hub with the same pcId; while its pcId is not known, the hub with the same name (as the
    /// dashboard did, M-46). Phones paired here are not on this list.
    /// </summary>
    public static IReadOnlyList<PcLink> Pair(IEnumerable<LinkedHubView> hubs, IEnumerable<DeviceEntry> here)
    {
        var links = hubs.Select(h => (Hub: (LinkedHubView?)h, Here: new List<DeviceEntry>())).ToList();
        foreach (var d in here.Where(d => d.Kind == DeviceKind.Pc))
        {
            var pcId = d.PcId is { Length: > 0 } id ? id : null;
            var i = links.FindIndex(l => pcId is not null
                ? string.Equals(l.Hub?.PcId ?? l.Here.Select(x => x.PcId).FirstOrDefault(x => x is { Length: > 0 }), pcId, StringComparison.Ordinal)
                : SameName(l.Hub?.PcName ?? l.Here[0].Name, d.Name));
            if (i < 0) links.Add((null, new List<DeviceEntry> { d }));
            else links[i].Here.Add(d);
        }
        return links.Select(l => new PcLink(l.Hub, l.Here)).ToList();
    }

    /// <summary>Settings › Phone's rows: <see cref="Pair"/>, each as one <see cref="LinkedPc"/>.</summary>
    public static IReadOnlyList<LinkedPc> Merge(IEnumerable<LinkedHubView> hubs, IEnumerable<DeviceEntry> here) =>
        Pair(hubs, here).Select(Summarize).ToList();

    /// <summary>
    /// What a linked PC is now, from both halves: online when either is, last seen at the later, and the state
    /// of the half that reports now.
    /// </summary>
    public static LinkedPc Summarize(PcLink link)
    {
        var h = link.Hub;
        var row = h is null
            ? new LinkedPc(link.Name, null, false, false, null, null, false, 0, Array.Empty<string>())
            : new LinkedPc(h.PcName, h.PcId, true, h.Online, h.LastContactAt, h.Status, h.Error == "rejected",
                h.Devices.Count(d => d.Kind == DeviceKind.Phone && d.DeviceId != h.SelfDeviceId), Array.Empty<string>());
        foreach (var d in link.Here)
        {
            var pcId = d.PcId is { Length: > 0 } id ? id : null;
            row = row with
            {
                PcId = row.PcId ?? pcId,
                Online = row.Online || d.Online,
                LastSeen = Later(row.LastSeen, d.LastSeenAt),
                Status = d.Online && !row.Online ? d.Status ?? row.Status : row.Status ?? d.Status,
                RevokeIds = pcId is null ? row.RevokeIds.Append(d.DeviceId).ToList() : row.RevokeIds,
            };
        }
        return row;
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
