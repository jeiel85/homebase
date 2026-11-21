using System.Security.Cryptography;
using System.Text;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// A phone paired here again after its app was reinstalled (or switched between the GitHub and Play builds)
/// is a new device: the new install has a new token and a new push endpoint, so nothing ties it to the old
/// entry, which stays listed (DECISIONS M-71; M-24 only catches a re-pair from the same install).
/// <para>
/// This finds the entries that look like that: a phone with the same name and model as a phone paired
/// later, and not heard from since. Two installs of one app can't run on one phone at once, so the old one
/// went quiet before the new one paired; a second phone of the same name that is still in use keeps
/// reporting and is left alone. One that has been off since then can't be told apart, so this only
/// marks them — the user confirms before anything is removed.
/// </para>
/// </summary>
public static class PhoneReinstalls
{
    /// <summary>Device ids of phones that look replaced by a later pairing of the same phone.</summary>
    public static IReadOnlySet<string> Superseded(IEnumerable<MobileDeviceInfo> devices)
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        foreach (var same in devices
                     .Where(d => d.Kind == DeviceKind.Phone && d.Name.Trim().Length > 0)
                     .GroupBy(d => GroupKeys.PhoneByName(d.Name, d.Model), StringComparer.Ordinal))
        {
            var newest = same.MaxBy(d => d.PairedAt)!;
            foreach (var d in same)
                if (d != newest && (d.LastSeenAt ?? d.PairedAt) <= newest.PairedAt) stale.Add(d.DeviceId);
        }
        return stale;
    }

    /// <summary>
    /// The same over what this PC has paired. Lists of devices shown to people (the group's device lists, where to send)
    /// leave these out (M-72): it only hides them — one that reports again is not marked any more and shows again.
    /// </summary>
    public static IReadOnlySet<string> Superseded(IMobileDeviceStore store) => Superseded(store.List());

    private const char Sep = (char)0x1f, EntryEnd = (char)0x1e;

    /// <summary>
    /// A short value that changes when what Settings › Phone shows of <paramref name="devices"/> changes: the
    /// entries, their names, models and kinds, the minute each was last heard from, and which look like old
    /// connections. The tab compares it with <see cref="BridgeStatus.DevicesVersion"/> on every status poll
    /// instead of reading the whole list each time.
    /// </summary>
    public static string ListVersion(IReadOnlyCollection<MobileDeviceInfo> devices)
    {
        var stale = Superseded(devices);
        var text = new StringBuilder();
        foreach (var d in devices.OrderBy(d => d.DeviceId, StringComparer.Ordinal))
        {
            var seenMinute = d.LastSeenAt is { } seen ? seen.ToUnixTimeSeconds() / 60 : -1;
            text.Append(d.DeviceId).Append(Sep).Append(d.Name).Append(Sep).Append(d.Model).Append(Sep)
                .Append(d.Kind).Append(Sep).Append(seenMinute).Append(Sep).Append(stale.Contains(d.DeviceId) ? '1' : '0')
                .Append(EntryEnd);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 8).ToLowerInvariant();
    }
}
