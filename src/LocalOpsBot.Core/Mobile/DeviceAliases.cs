using System.Text;
using System.Text.RegularExpressions;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// One alias of the device group (DECISIONS M-50). <c>Key</c> names the device the way the group does
/// (<see cref="GroupKeys"/>): <c>pc:&lt;pcId&gt;</c>, or <c>phone-name:&lt;name&gt;|&lt;model&gt;</c> for a phone
/// (phones have a different device id on every PC). <c>Name</c> is what the device reported when it was
/// named, so places that know only a sender's name (a relayed notification) still find it. <c>Alias</c>
/// null means the alias was cleared — kept so the clearing reaches every member. <c>At</c> is when it was
/// set or cleared (by the clock of the device where the user did it); the latest wins.
/// </summary>
public sealed record DeviceAlias(string Key, string Name, string? Alias, DateTimeOffset At);

/// <summary><c>POST /api/v1/group/alias</c> body: aliases a member set (or learned), to merge (M-50).</summary>
public sealed record GroupAliasRequest(int Schema, IReadOnlyList<DeviceAlias> Aliases);

/// <summary><c>POST /api/v1/group/alias</c> answer: how many entries changed here.</summary>
public sealed record GroupAliasResponse(int Schema, int Changed);

/// <summary>Bridge <c>aliases.set</c> (Tray → Agent): the user named a device on this PC (M-50).</summary>
public sealed record AliasSetRequest(string Key, string Name, string? Alias);

/// <summary>Bridge <c>aliases</c> push (Agent → Tray): the whole table, cleared entries included.</summary>
public sealed record AliasListPayload(IReadOnlyList<DeviceAlias> Aliases);

/// <summary>
/// The group's aliases (DECISIONS M-50, after M-36 and M-49): names the user gave PCs and phones, shared by
/// every member. Each PC's Agent keeps one table, serves it with its group answer, and merges the tables of
/// the PCs it syncs with; phones merge it into their PC list. Per key the newest entry wins, a tie going to
/// the larger alias text, so every member ends with the same table whatever order changes arrive in.
/// </summary>
public sealed partial class DeviceAliasTable
{
    /// <summary>Longest alias kept (code points), as on the phone (M-36): the device list shows one line.</summary>
    public const int MaxLength = 40;

    /// <summary>Most entries kept; past it, the oldest cleared ones go first, then the oldest aliases.</summary>
    public const int MaxEntries = 256;

    /// <summary>Most entries accepted in one request.</summary>
    public const int MaxPerRequest = 64;

    /// <summary>Longest device name kept with an entry.</summary>
    public const int MaxNameLength = 100;

    /// <summary>A cleared alias is remembered this long, so a member that was off meanwhile does not bring it back.</summary>
    public static readonly TimeSpan ClearedKeep = TimeSpan.FromDays(90);

    /// <summary>Entries stamped further ahead than this (a device with a wrong clock) are refused.</summary>
    public static readonly TimeSpan FutureSlack = TimeSpan.FromDays(1);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceAlias> _entries = new(StringComparer.Ordinal);

    public DeviceAliasTable(TimeProvider time, IEnumerable<DeviceAlias>? initial = null)
    {
        _time = time;
        if (initial is not null) Merge(initial, raise: false);
    }

    /// <summary>Raised after the table changed (to persist it, tell the Tray, redraw).</summary>
    public event Action? Changed;

    /// <summary>Entries kept, cleared ones included.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// The user named the device <paramref name="key"/> (reporting <paramref name="name"/>) here, now: see
    /// <see cref="Normalize"/>; blank or the device's own name clears it. The new entry, or null when nothing
    /// changed or the key is not a group key.
    /// </summary>
    public DeviceAlias? Set(string key, string name, string? input)
    {
        if (!GroupKeys.IsValid(key) || Clean(name) is not { } cleanName) return null;
        var alias = Normalize(input, cleanName);
        DeviceAlias entry;
        lock (_gate)
        {
            var old = _entries.GetValueOrDefault(key);
            if (old is not null && old.Alias == alias) return null;
            if (old is null && alias is null) return null; // clearing what was never set
            // Strictly after the entry it replaces, even if this clock is behind the one that set it.
            var at = Ms(_time.GetUtcNow());
            if (old is not null && at <= old.At) at = old.At.AddMilliseconds(1);
            entry = new DeviceAlias(key, cleanName, alias, at);
            _entries[key] = entry;
            Trim();
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Takes what another member knows; returns how many entries changed here.</summary>
    public int Merge(IEnumerable<DeviceAlias> other) => Merge(other, raise: true);

    private int Merge(IEnumerable<DeviceAlias> other, bool raise)
    {
        var changed = 0;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var raw in other)
            {
                if (Valid(raw, now) is not { } e) continue;
                if (_entries.TryGetValue(e.Key, out var old) && !Wins(e, old)) continue;
                _entries[e.Key] = e;
                changed++;
            }
            if (changed > 0) Trim();
        }
        if (changed > 0 && raise) Changed?.Invoke();
        return changed;
    }

    /// <summary>Replaces the whole table (the Tray's copy of its Agent's); raises <see cref="Changed"/> when it differs.</summary>
    public void Replace(IEnumerable<DeviceAlias> all)
    {
        var now = _time.GetUtcNow();
        bool same;
        lock (_gate)
        {
            var next = new Dictionary<string, DeviceAlias>(StringComparer.Ordinal);
            foreach (var raw in all)
                if (Valid(raw, now) is { } e && (!next.TryGetValue(e.Key, out var old) || Wins(e, old))) next[e.Key] = e;
            same = next.Count == _entries.Count && next.All(kv => _entries.TryGetValue(kv.Key, out var e) && e == kv.Value);
            if (!same)
            {
                _entries.Clear();
                foreach (var kv in next) _entries[kv.Key] = kv.Value;
            }
        }
        if (!same) Changed?.Invoke();
    }

    /// <summary>Every entry, cleared ones included (for members to merge), oldest first.</summary>
    public IReadOnlyList<DeviceAlias> List()
    {
        lock (_gate)
        {
            Trim();
            return _entries.Values.OrderBy(e => e.At).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>The alias of <paramref name="key"/>, or null (none, or cleared).</summary>
    public string? Get(string? key)
    {
        if (key is null) return null;
        lock (_gate) return _entries.GetValueOrDefault(key)?.Alias;
    }

    /// <summary>
    /// The alias to show for a device: by its <paramref name="key"/> when known and the table has it (a
    /// cleared entry means none), else by the <paramref name="kind"/> and <paramref name="name"/> it reported —
    /// all a relayed notification or a device seen through another PC tells.
    /// </summary>
    public string? Resolve(string? key, string? kind, string? name)
    {
        lock (_gate)
        {
            if (key is not null && _entries.TryGetValue(key, out var byKey)) return byKey.Alias;
            if (Clean(name) is not { } n) return null;
            var pc = DeviceKind.Normalize(kind) == DeviceKind.Pc;
            return _entries.Values
                .Where(e => e.Alias is not null && IsPcKey(e.Key) == pc && string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))
                .MaxBy(e => e.At)?.Alias;
        }
    }

    /// <summary>What to show for a device: its alias, else <paramref name="name"/>.</summary>
    public string Show(string? key, string? kind, string name) => Resolve(key, kind, name) ?? name;

    /// <summary>
    /// The alias to keep for <paramref name="input"/>: trimmed, inner runs of whitespace (a pasted newline)
    /// made one space, cut to <see cref="MaxLength"/> code points. Blank, or the same as the device's own
    /// <paramref name="name"/>, means none. The phone app's rule (M-36).
    /// </summary>
    public static string? Normalize(string? input, string name)
    {
        if (input is null) return null;
        var cleaned = Cut(Whitespace().Replace(input.Trim(), " "), MaxLength);
        return cleaned.Length == 0 || cleaned == name.Trim() ? null : cleaned;
    }

    /// <summary>Code points in <paramref name="s"/> (an emoji counts once), for the dialog's counter.</summary>
    public static int Length(string s) => s.EnumerateRunes().Count();

    /// <summary>The first <paramref name="max"/> code points, so an emoji at the edge is not split in half.</summary>
    public static string Cut(string s, int max)
    {
        if (Length(s) <= max) return s;
        var sb = new StringBuilder();
        foreach (var r in s.EnumerateRunes().Take(max)) sb.Append(r.ToString());
        return sb.ToString().TrimEnd();
    }

    /// <summary>True when <paramref name="a"/> replaces <paramref name="b"/>: newer, or as new with the larger alias text.</summary>
    public static bool Wins(DeviceAlias a, DeviceAlias b) =>
        a.At > b.At || (a.At == b.At && string.CompareOrdinal(a.Alias ?? "", b.Alias ?? "") > 0);

    private static bool IsPcKey(string key) => key.StartsWith("pc:", StringComparison.Ordinal);

    /// <summary>The entry held to this table's rules, or null when it can't be kept.</summary>
    private static DeviceAlias? Valid(DeviceAlias? e, DateTimeOffset now)
    {
        if (e is null || !GroupKeys.IsValid(e.Key) || Clean(e.Name) is not { } name) return null;
        if (e.At > now + FutureSlack) return null;
        if (e.Alias is null && now - e.At > ClearedKeep) return null;
        return new DeviceAlias(e.Key, name, Normalize(e.Alias, name), Ms(e.At));
    }

    private static string? Clean(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) ? null : Cut(n, MaxNameLength);
    }

    /// <summary>Millisecond precision, what the phone keeps, so a round trip compares equal.</summary>
    private static DateTimeOffset Ms(DateTimeOffset t) => DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds());

    /// <summary>Drops cleared entries past <see cref="ClearedKeep"/>, then the oldest beyond <see cref="MaxEntries"/>.</summary>
    private void Trim()
    {
        var now = _time.GetUtcNow();
        foreach (var gone in _entries.Values.Where(e => e.Alias is null && now - e.At > ClearedKeep).ToList()) _entries.Remove(gone.Key);
        if (_entries.Count <= MaxEntries) return;
        foreach (var gone in _entries.Values.OrderBy(e => e.Alias is null ? 0 : 1).ThenBy(e => e.At).Take(_entries.Count - MaxEntries).ToList())
            _entries.Remove(gone.Key);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
