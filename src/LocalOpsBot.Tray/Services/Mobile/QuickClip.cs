using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>What quick text (M-78) goes straight onto the clipboard here; the rest waits behind a toast.</summary>
public enum QuickScope
{
    /// <summary>Any text, as it came.</summary>
    All,
    /// <summary>The first <c>http(s)://</c> link in the text — that link alone.</summary>
    Links,
    /// <summary>The first link to one of the listed sites — that link alone (the default: Threads, for Threads Archiver).</summary>
    Sites,
}

/// <summary>This Windows user's quick-receive range (<c>docs/QUICK_SEND.md</c> §6.1).</summary>
internal sealed record QuickClipPrefs(QuickScope Scope, IReadOnlyList<string> Sites)
{
    public static readonly IReadOnlyList<string> DefaultSites = ["threads.com", "threads.net"];
    public static readonly QuickClipPrefs Default = new(QuickScope.Sites, DefaultSites);
}

/// <summary>
/// What of quick text (M-78) goes onto the clipboard under the range the user chose: <see cref="QuickScope.All"/> the whole
/// text; <see cref="QuickScope.Links"/> only its first <c>http(s)://</c> link; <see cref="QuickScope.Sites"/> only its first
/// link to a listed site — the site itself or any subdomain, and also a bare <c>threads.com/…</c> token without the scheme,
/// as Threads Archiver takes it (put on the clipboard with <c>https://</c> in front). Taking the link alone means text
/// wrapped around a link (a command line, say) never reaches the clipboard through these ranges. Links are only looked for
/// in text up to <see cref="MaxMatchBytes"/> bytes of UTF-8 — the Archiver's bound — and longer text is out of range.
/// </summary>
internal static class QuickClipRange
{
    public const int MaxMatchBytes = 4096;
    public const int MaxSites = 50;
    private static readonly IReadOnlyList<string> ThreadsSites = QuickClipPrefs.DefaultSites;

    /// <summary>What to put on the clipboard; null when the text is out of range (held behind a toast).</summary>
    public static string? Pick(string text, QuickClipPrefs prefs) => prefs.Scope switch
    {
        QuickScope.All => text,
        QuickScope.Links => FirstLink(text),
        _ => FirstSiteLink(text, prefs.Sites),
    };

    public static bool InRange(string text, QuickClipPrefs prefs) => Pick(text, prefs) is not null;

    /// <summary>The first <c>http://</c> or <c>https://</c> link in the text, trimmed like the Archiver trims it.</summary>
    public static string? FirstLink(string text)
    {
        if (TooLong(text)) return null;
        return Tokens(text).FirstOrDefault(t => Host(t) is not null);
    }

    /// <summary>The first link to one of <paramref name="sites"/> (or a subdomain of one); a bare one gets <c>https://</c>.</summary>
    public static string? FirstSiteLink(string text, IReadOnlyList<string> sites)
    {
        if (sites.Count == 0 || TooLong(text)) return null;
        foreach (var token in Tokens(text))
        {
            if (Host(token) is { } host)
            {
                if (sites.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal))) return token;
                continue;
            }
            // threads.com/@a/post/b — a link without its scheme counts for a listed site.
            var lower = token.ToLowerInvariant();
            if (lower.StartsWith("www.", StringComparison.Ordinal)) lower = lower[4..];
            if (sites.Any(s => lower.StartsWith(s + "/", StringComparison.Ordinal))) return "https://" + token;
        }
        return null;
    }

    /// <summary>Text with a Threads link — for the Archiver check.</summary>
    public static bool HasThreadsLink(string text) => FirstSiteLink(text, ThreadsSites) is not null;

    /// <summary>Whether Threads links fall in the range at all, so the Archiver's state is worth showing.</summary>
    public static bool CoversThreads(QuickClipPrefs prefs) =>
        prefs.Scope != QuickScope.Sites || prefs.Sites.Any(s => ThreadsSites.Contains(s));

    /// <summary>Longer than the Archiver reads (it compares UTF-8 bytes).</summary>
    public static bool TooLong(string text) => text.Length > MaxMatchBytes || Encoding.UTF8.GetByteCount(text) > MaxMatchBytes;

    /// <summary>
    /// The site list as typed, one per line (or comma-separated): lower-cased domains with any scheme, <c>www.</c>, path
    /// or port taken off; anything that is not a domain is dropped. At most <see cref="MaxSites"/>, no duplicates.
    /// </summary>
    public static IReadOnlyList<string> NormalizeSites(string typed)
    {
        var sites = new List<string>();
        foreach (var raw in typed.Split(['\r', '\n', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim().ToLowerInvariant();
            var scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s[(scheme + 3)..];
            var end = s.IndexOfAny(['/', '?', '#', ':']);
            if (end >= 0) s = s[..end];
            s = s.Trim('.');
            if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
            if (!IsDomain(s) || sites.Contains(s)) continue;
            sites.Add(s);
            if (sites.Count == MaxSites) break;
        }
        return sites;
    }

    private static bool IsDomain(string s) =>
        s.Length is > 3 and <= 253 && s.Contains('.') && s.Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));

    /// <summary>Whitespace-separated words with quotes, brackets and trailing punctuation taken off, like the Archiver.</summary>
    private static IEnumerable<string> Tokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimStart('"', '\'', '<', '(', '[').TrimEnd('"', '\'', '>', ')', ']', '.', ',', '!', '?', ';', ':'))
            .Where(t => t.Length > 0);

    /// <summary>The lower-cased host of an <c>http(s)://</c> token; null for anything else.</summary>
    private static string? Host(string token)
    {
        if (!token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        return Uri.TryCreate(token, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host.ToLowerInvariant().TrimEnd('.') : null;
    }
}

/// <summary>How a held quick text's toast reads (M-78).</summary>
internal enum HeldNotice
{
    /// <summary>Kept: the toast's [Copy to the clipboard] puts it there.</summary>
    CopyButton,
    /// <summary>Private (a password manager's): neither pasted nor kept.</summary>
    NotKeptSensitive,
    /// <summary>The store couldn't keep it: not taken, the phone is told to send again.</summary>
    NotKeptFailed,
}

/// <summary>
/// The tray's quick-text decisions (M-78), apart from the clipboard, the store and the toasts so they can be tested: what to
/// paste, whether a paste is too late for the Agent's answer, and what each outcome tells the Agent and the user.
/// </summary>
internal static class QuickClipDecision
{
    /// <summary>
    /// The Agent waits 10 s for the tray's answer (<c>MobileBridgeServer.ClipDeliverWait</c>); text that has waited longer
    /// than this in the tray is not pasted late, after the phone has already been told "busy, send again".
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(7);

    public static bool TooLate(DateTimeOffset received, DateTimeOffset now) => now - received > Deadline;

    /// <summary>After a paste attempt: the answer, and whether to warn that Threads Archiver isn't running.</summary>
    public static (TrayClipOutcome Outcome, bool WarnArchiver) Pasted(bool ok, string? archiver) =>
        ok ? (new TrayClipOutcome(true, false, archiver), archiver == ClipArchiver.Stopped) : (TrayClipOutcome.NotPasted, false);

    /// <summary>
    /// Out of range: private text is not kept (taken, held, no button); kept text is held behind [Copy]; text the store couldn't
    /// keep is not taken at all — a dead button would lose it, while "not pasted" lets the phone send it again.
    /// </summary>
    public static (TrayClipOutcome Outcome, HeldNotice Notice) Held(bool sensitive, bool stored, string? archiver) =>
        sensitive ? (new TrayClipOutcome(true, true, archiver), HeldNotice.NotKeptSensitive)
        : stored ? (new TrayClipOutcome(true, true, archiver), HeldNotice.CopyButton)
        : (TrayClipOutcome.NotPasted, HeldNotice.NotKeptFailed);
}

/// <summary>The quick-receive range in <c>%LOCALAPPDATA%\Homebase\quick-clip.json</c>, per Windows user, no admin needed.</summary>
internal static class QuickClipSettings
{
    private sealed record Stored(string? Scope, IReadOnlyList<string>? Sites);

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Tests point this elsewhere.</summary>
    internal static string PathName { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "quick-clip.json");

    /// <summary>The stored range; a missing, broken or empty site list means the default one (Threads).</summary>
    public static QuickClipPrefs Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(PathName)) return QuickClipPrefs.Default;
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(PathName), Json);
                var scope = stored?.Scope switch { "all" => QuickScope.All, "links" => QuickScope.Links, _ => QuickScope.Sites };
                var sites = stored?.Sites is { } list ? QuickClipRange.NormalizeSites(string.Join('\n', list)) : [];
                return new QuickClipPrefs(scope, sites.Count > 0 ? sites : QuickClipPrefs.DefaultSites);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return QuickClipPrefs.Default; // a broken file just means the defaults
            }
        }
    }

    public static void Save(QuickClipPrefs prefs)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                var scope = prefs.Scope switch { QuickScope.All => "all", QuickScope.Links => "links", _ => "sites" };
                var tmp = PathName + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored(scope, prefs.Sites), Json));
                File.Move(tmp, PathName, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TrayLog.Warn("Clipboard", $"The quick-receive range couldn't be saved: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Quick text kept off the clipboard (M-78) until the user clicks [Copy to the clipboard] on its toast: the newest
/// <see cref="Max"/>, for <see cref="Lifetime"/>, in <c>%LOCALAPPDATA%\Homebase\held-clips.dat</c> encrypted for this Windows
/// user (DPAPI). The toast's link carries only the id, so a web page's <c>homebase:</c> link can't put text of its choosing
/// on the clipboard. Shared between the tray and the short-lived <c>--toast</c> process, under a named mutex — never touched
/// without it: a store that can't be locked, read or written reports failure instead.
/// </summary>
internal static class HeldClips
{
    public const int Max = 20;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly byte[] Entropy = "homebase held clips v1"u8.ToArray();
    /// <summary>The lock shared by the tray and the <c>--toast</c> process (tests use their own).</summary>
    internal static string MutexName { get; set; } = @"Local\Homebase.HeldClips";
    /// <summary>How long a change waits for the store's lock (tests shorten it).</summary>
    internal static TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(3);

    internal sealed record Entry(string Id, string Text, string From, DateTimeOffset At);

    /// <summary>Tests point this elsewhere.</summary>
    internal static string PathName { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "held-clips.dat");

    /// <summary>Keeps the text; false when it couldn't be kept (then no toast may offer it).</summary>
    public static bool Add(string id, string text, string from, DateTimeOffset now) => Put(new Entry(id, text, from, now), now);

    /// <summary>Keeps <paramref name="entry"/> as it is (its time and sender) — also to put one back that couldn't be pasted.</summary>
    public static bool Put(Entry entry, DateTimeOffset now) => Update(list => (Added(list, entry, now), true));

    /// <summary>The held entry by <paramref name="id"/>, taken out of the store; null when unknown, expired or unreachable.</summary>
    public static Entry? Take(string id, DateTimeOffset now)
    {
        if (!ClipProtocol.IsValidId(id)) return null;
        Entry? taken = null;
        var ok = Update(list =>
        {
            var (kept, found) = Taking(list, id, now);
            taken = found;
            return (kept, found is not null || kept.Count != list.Count);
        });
        return ok ? taken : null;
    }

    /// <summary>The list with <paramref name="entry"/> in front, expired ones out, at most <see cref="Max"/>.</summary>
    internal static List<Entry> Added(IReadOnlyList<Entry> list, Entry entry, DateTimeOffset now) =>
        new[] { entry }.Concat(list.Where(e => e.Id != entry.Id)).Where(e => now - e.At < Lifetime).Take(Max).ToList();

    /// <summary>The list without <paramref name="id"/> and expired ones, and the entry taken (null when not there).</summary>
    internal static (List<Entry> Kept, Entry? Taken) Taking(IReadOnlyList<Entry> list, string id, DateTimeOffset now)
    {
        var live = list.Where(e => now - e.At < Lifetime).ToList();
        var taken = live.FirstOrDefault(e => e.Id == id);
        if (taken is not null) live.Remove(taken);
        return (live, taken);
    }

    /// <summary>A change under the lock; false when the lock wasn't had in time or the file couldn't be read or written.</summary>
    private static bool Update(Func<IReadOnlyList<Entry>, (List<Entry> List, bool Changed)> change)
    {
        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(LockWait); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            TrayLog.Warn("Clipboard", "Held clipboard text: the store stayed locked");
            return false;
        }
        try
        {
            var (list, changed) = change(Read());
            if (changed) Write(list);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            TrayLog.Warn("Clipboard", $"Held clipboard text couldn't be kept: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static List<Entry> Read()
    {
        if (!File.Exists(PathName)) return new List<Entry>();
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(PathName), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return new List<Entry>(); // another user's or damaged: start over
        }
        try { return JsonSerializer.Deserialize<List<Entry>>(plain) ?? new List<Entry>(); }
        catch (JsonException) { return new List<Entry>(); }
    }

    private static void Write(List<Entry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var tmp = PathName + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(list), Entropy, DataProtectionScope.CurrentUser));
        File.Move(tmp, PathName, overwrite: true);
    }
}

/// <summary>Whether Threads Archiver (<c>threads-archiver-desktop.exe</c>) runs in this Windows session (M-78).</summary>
internal static class ArchiverProbe
{
    public const string ProcessName = "threads-archiver-desktop";

    public static bool IsRunning()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        var found = false;
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                try { found |= p.SessionId == session; }
                catch (InvalidOperationException) { /* it just exited */ }
            }
        }
        return found;
    }

    public static string State() => IsRunning() ? ClipArchiver.Running : ClipArchiver.Stopped;
}

/// <summary>
/// Quick text put on the clipboard one at a time, at least <see cref="Gap"/> after the previous one: Threads Archiver looks at
/// the clipboard every 0.5 s and would miss the first of two links pasted closer together.
/// </summary>
internal sealed class QuickPacer
{
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Task> _delay;
    private DateTimeOffset? _last;

    public QuickPacer(Func<DateTimeOffset>? now = null, Func<TimeSpan, Task>? delay = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? (d => Task.Delay(d));
    }

    /// <summary>Runs <paramref name="put"/> after waiting out the gap; a true result starts the next gap.</summary>
    public async Task<bool> RunAsync(Func<Task<bool>> put)
    {
        await _gate.WaitAsync();
        try
        {
            if (_last is { } last && last + Gap - _now() is { } wait && wait > TimeSpan.Zero) await _delay(wait);
            var ok = await put();
            if (ok) _last = _now();
            return ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="RunAsync"/> for text the tray got at <paramref name="received"/>: once its turn comes, text older than
    /// <see cref="QuickClipDecision.Deadline"/> is not pasted (false, <c>Late</c>) — the Agent has answered "busy" by then.
    /// </summary>
    public async Task<(bool Ok, bool Late)> RunBeforeAsync(DateTimeOffset received, Func<Task<bool>> put)
    {
        var late = false;
        var ok = await RunAsync(() =>
        {
            if (!QuickClipDecision.TooLate(received, _now())) return put();
            late = true;
            return Task.FromResult(false);
        });
        return (ok, late);
    }
}

/// <summary>Quick text received since the tray started (M-78), for the dashboard's line: today's count and the last time.</summary>
internal static class QuickClipStats
{
    private static readonly object Gate = new();
    private static DateOnly _day;
    private static int _today;

    public static DateTimeOffset? Last { get; private set; }

    public static void Record(DateTimeOffset now)
    {
        lock (Gate)
        {
            var day = DateOnly.FromDateTime(now.LocalDateTime);
            if (day != _day) (_day, _today) = (day, 0);
            _today++;
            Last = now;
        }
    }

    public static int Today(DateTimeOffset now)
    {
        lock (Gate) return DateOnly.FromDateTime(now.LocalDateTime) == _day ? _today : 0;
    }
}
