namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// The device group (DECISIONS M-47): connecting a device to any member means joining the group, so
/// every PC ends up linked with every other PC and every phone paired with every PC. Members introduce
/// each other by carrying a fresh one-time pairing offer from one PC to another over channels that are
/// already authenticated — no new trust is created, only extended inside the group.
/// </summary>
public static class GroupPaths
{
    /// <summary><c>GET</c>: this PC and the PCs it is linked with.</summary>
    public const string Group = "/api/v1/group";

    /// <summary>
    /// <c>POST {kind?}</c>: a fresh one-time pairing offer of this PC, for a member to hand to another. Its
    /// code pairs a PC only, unless a linked PC asks for <c>kind: phone</c> to introduce a phone (v0.60.2,
    /// see <see cref="GroupOfferRequest"/>).
    /// </summary>
    public const string Offer = "/api/v1/group/offer";

    /// <summary><c>POST {offer}</c>: link this PC to the PC behind the offer.</summary>
    public const string Join = "/api/v1/group/join";

    /// <summary><c>POST {pcId}</c>: a fresh offer of a PC this one holds a token for.</summary>
    public const string Introduce = "/api/v1/group/introduce";

    /// <summary><c>POST {key}</c>: take a device out of the group on this PC.</summary>
    public const string Remove = "/api/v1/group/remove";

    /// <summary><c>POST {aliases}</c>: aliases a member set, to merge here (M-51).</summary>
    public const string Alias = "/api/v1/group/alias";
}

/// <summary>A PC of the group as one member sees it. <c>Token</c>: this member holds its token (it linked to it).</summary>
public sealed record GroupMember(string PcId, string PcName, bool Token);

/// <summary>
/// <c>GET /api/v1/group</c>: the answering PC (<c>PcId</c>, <c>PcName</c>), the other PCs it is linked
/// with either way, what was taken out of the group lately (<see cref="GroupRemoval"/>), and the group's
/// aliases (v0.38.0, <see cref="DeviceAlias"/>, cleared ones included).
/// </summary>
public sealed record GroupResponse(
    int Schema,
    string PcId,
    string PcName,
    IReadOnlyList<GroupMember> Pcs,
    IReadOnlyList<GroupRemoval>? Removed = null,
    IReadOnlyList<DeviceAlias>? Aliases = null);

public sealed record GroupJoinRequest(int Schema, PairingOffer Offer);

/// <summary><c>Error</c>: <c>self</c>, <c>removed</c>, <c>unreachable</c>, <c>rejected</c>, <c>off</c>.</summary>
public sealed record GroupJoinResponse(int Schema, bool Linked, bool Already, string? PcName, string? Error);

public sealed record GroupIntroduceRequest(int Schema, string PcId);

/// <summary>
/// <c>POST /api/v1/group/offer</c> body. <c>Kind</c>: the kind of device the offer's code may pair —
/// <see cref="DeviceKind.Pc"/> when absent (older members send none). A member asks for
/// <see cref="DeviceKind.Phone"/> only when a phone paired with it asked to be introduced; only a linked
/// PC may ask for that (the group already trusts its PCs to introduce, M-47; DECISIONS M-77).
/// </summary>
public sealed record GroupOfferRequest(int Schema, string? Kind = null)
{
    /// <summary>The kind the offer's code pairs: a phone only when asked for in so many words.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string AllowedKind => string.Equals(Kind, DeviceKind.Phone, StringComparison.Ordinal) ? DeviceKind.Phone : DeviceKind.Pc;
}

public sealed record GroupRemoveRequest(int Schema, string Key);

/// <summary>
/// A device taken out of the group, kept for <see cref="GroupTombstones.Keep"/> so a member that was
/// offline meanwhile does not bring it back. <c>Key</c>: <c>pc:&lt;pcId&gt;</c>, or for a phone
/// <c>phone:&lt;push hash&gt;</c> or <c>phone-name:&lt;name&gt;|&lt;model&gt;</c> (phones have a different
/// device id on every PC).
/// </summary>
public sealed record GroupRemoval(string Key, DateTimeOffset At);

public static class GroupKeys
{
    public static string Pc(string pcId) => "pc:" + pcId;

    public static string PhoneByPush(string pushHash) => "phone:" + pushHash;

    public static string PhoneByName(string name, string? model) => "phone-name:" + name.Trim().ToLowerInvariant() + "|" + (model ?? "").Trim().ToLowerInvariant();

    public static string? PcIdOf(string key) => key.StartsWith("pc:", StringComparison.Ordinal) ? key[3..] : null;

    public static bool IsValid(string? key) => key is { Length: > 3 and <= 200 }
        && (key.StartsWith("pc:", StringComparison.Ordinal) || key.StartsWith("phone:", StringComparison.Ordinal)
            || key.StartsWith("phone-name:", StringComparison.Ordinal));
}

/// <summary>
/// What was taken out of the group, newest time per key, for <see cref="Keep"/>. Members merge each
/// other's lists while they sync, so a removal reaches PCs that were offline when it happened. Automatic
/// joins and introductions refuse a removed key; pairing by hand (QR, link, code) always works and clears it.
/// </summary>
public sealed class GroupTombstones
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _removed = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public GroupTombstones(TimeProvider time, IEnumerable<GroupRemoval>? initial = null)
    {
        _time = time;
        if (initial is not null) Merge(initial);
    }

    /// <summary>Raised after the list changed (to persist it).</summary>
    public event Action? Changed;

    public void Add(string key)
    {
        lock (_gate) _removed[key] = _time.GetUtcNow();
        Changed?.Invoke();
    }

    public void Clear(string key)
    {
        bool removed;
        lock (_gate) removed = _removed.Remove(key);
        if (removed) Changed?.Invoke();
    }

    /// <summary>Adds what another member removed; returns the keys new to this list.</summary>
    public IReadOnlyList<string> Merge(IEnumerable<GroupRemoval> other)
    {
        var fresh = new List<string>();
        var cutoff = _time.GetUtcNow() - Keep;
        lock (_gate)
        {
            foreach (var r in other)
            {
                if (r.At < cutoff || !GroupKeys.IsValid(r.Key)) continue;
                if (_removed.TryGetValue(r.Key, out var at) && at >= r.At) continue;
                if (!_removed.ContainsKey(r.Key)) fresh.Add(r.Key);
                _removed[r.Key] = r.At;
            }
        }
        if (fresh.Count > 0) Changed?.Invoke();
        return fresh;
    }

    public bool IsRemoved(string key)
    {
        lock (_gate) return _removed.TryGetValue(key, out var at) && _time.GetUtcNow() - at <= Keep;
    }

    public IReadOnlyList<GroupRemoval> List()
    {
        var cutoff = _time.GetUtcNow() - Keep;
        lock (_gate)
        {
            foreach (var old in _removed.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) _removed.Remove(old);
            return _removed.Select(kv => new GroupRemoval(kv.Key, kv.Value)).OrderBy(r => r.At).ToList();
        }
    }
}
