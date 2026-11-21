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
}
