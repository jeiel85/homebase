using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Protocol.Messaging;

namespace LocalOpsBot.Core.Mobile;

/// <summary>Values of <see cref="AgentAlert.Kind"/>, which are also the notification categories (DECISIONS M-45).</summary>
public static class AgentAlertKind
{
    /// <summary>A session stopped to ask the user (a permission prompt or a question).</summary>
    public const string Waiting = "agent-waiting";

    /// <summary>A session finished a turn that ran at least <see cref="AgentAlertDetector.MinTurnForDone"/>.</summary>
    public const string Done = "agent-done";

    public static bool IsKnown(string? kind) => kind is Waiting or Done;
}

/// <summary>One AI session as the alert watcher sees it on a pass.</summary>
/// <param name="Key">Stable per session: tool and session id.</param>
public sealed record AgentState(
    string Key,
    string App,
    string Title,
    string? Project,
    string? Status,
    string? WaitingFor,
    DateTimeOffset? StatusSince);

/// <summary>What the Tray asks the Agent to send (bridge op <c>agent.alert</c>).</summary>
public sealed record AgentAlert(string Kind, string App, string Title, string? Project, string? Detail);

/// <summary>
/// Turns successive views of the AI sessions into alerts (DECISIONS M-45): a session that starts
/// waiting for the user, and one that goes from busy to idle after working at least
/// <see cref="MinTurnForDone"/> (a short answer the user just watched needs no notification).
/// The first view of a session only records it, so starting the Tray does not replay old states —
/// except a session already waiting for at most <see cref="FirstSightWaiting"/>: one that started
/// waiting while the Tray restarted (an update) would otherwise never be told (#61).
/// Pure: the caller reads the files, decides whether the user is away and sends the alerts.
/// </summary>
public sealed class AgentAlertDetector
{
    public static readonly TimeSpan MinTurnForDone = TimeSpan.FromSeconds(60);

    /// <summary>How recently a session seen for the first time must have started waiting to be alerted.</summary>
    public static readonly TimeSpan FirstSightWaiting = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (string? Status, DateTimeOffset BusySince)> _last = new(StringComparer.Ordinal);

    public IReadOnlyList<AgentAlert> Update(IEnumerable<AgentState> states, DateTimeOffset now)
    {
        var alerts = new List<AgentAlert>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in states)
        {
            if (!seen.Add(s.Key)) continue;
            var known = _last.TryGetValue(s.Key, out var prev);
            var busySince = s.Status == AgentStatus.Busy
                ? (known && prev.Status == AgentStatus.Busy ? prev.BusySince : s.StatusSince ?? now)
                : prev.BusySince;
            _last[s.Key] = (s.Status, busySince);
            if (!known)
            {
                // Without a start time its age is unknown: recorded only, as before.
                if (s.Status == AgentStatus.Waiting && s.StatusSince is { } since
                    && now - since <= FirstSightWaiting && since <= now + TimeSpan.FromMinutes(1))
                    alerts.Add(new AgentAlert(AgentAlertKind.Waiting, s.App, s.Title, s.Project, s.WaitingFor));
                continue;
            }
            if (prev.Status == s.Status) continue;

            if (s.Status == AgentStatus.Waiting)
                alerts.Add(new AgentAlert(AgentAlertKind.Waiting, s.App, s.Title, s.Project, s.WaitingFor));
            else if (s.Status == AgentStatus.Idle && prev.Status == AgentStatus.Busy
                     && (s.StatusSince ?? now) - prev.BusySince >= MinTurnForDone)
                alerts.Add(new AgentAlert(AgentAlertKind.Done, s.App, s.Title, s.Project,
                    Minutes((s.StatusSince ?? now) - prev.BusySince)));
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
        return alerts;
    }

    public static string Minutes(TimeSpan t) =>
        t.TotalMinutes < 1 ? "1분 미만"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}분"
        : $"{(int)t.TotalHours}시간 {t.Minutes}분";
}

/// <summary>
/// The Agent's side of <c>agent.alert</c> (DECISIONS M-45): any process of the signed-in user can
/// write to the bridge pipe, so what arrives is trimmed to the protocol's limits and rate-limited
/// before it costs a push (ntfy allows 250 a day for all categories together): the same alert at
/// most once per <see cref="Cooldown"/>, and at most <see cref="DailyLimit"/> a day.
/// </summary>
public sealed class AgentAlertGate
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public const int DailyLimit = 60;

    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);
    private DateOnly _day;
    private int _sentToday;

    /// <summary>The alert within the protocol's limits, or null when it is not one.</summary>
    public static AgentAlert? Sanitize(AgentAlert? a)
    {
        if (a is null || !AgentAlertKind.IsKnown(a.Kind)) return null;
        var app = Clip(a.App, 40);
        var title = Clip(a.Title, 200);
        if (app is null || title is null) return null;
        return new AgentAlert(a.Kind, app, title, Clip(a.Project, 260), Clip(a.Detail, 300));
    }

    /// <summary>Whether <paramref name="a"/> may be sent now; counts it when it may.</summary>
    public bool TryPass(AgentAlert a, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (today != _day)
        {
            _day = today;
            _sentToday = 0;
        }
        var key = $"{a.Kind}|{a.App}|{a.Title}|{a.Project}";
        if (_lastSent.TryGetValue(key, out var last) && now - last < Cooldown) return false;
        if (_sentToday >= DailyLimit) return false;
        foreach (var old in _lastSent.Where(kv => now - kv.Value >= Cooldown).Select(kv => kv.Key).ToList()) _lastSent.Remove(old);
        _lastSent[key] = now;
        _sentToday++;
        return true;
    }

    /// <summary>
    /// The push: the header reads "PC · Claude Code" (the app is the source), the title is the
    /// session, the body says what happened. Waiting pops up as a warning; done is information.
    /// Encrypted channels only — a session title is the user's work, not something for Telegram.
    /// </summary>
    public static OutboundNotification ToNotification(AgentAlert a, DateTimeOffset now)
    {
        var what = a.Kind == AgentAlertKind.Waiting
            ? (a.Detail is { } d ? $"입력을 기다립니다: {d}" : "입력을 기다립니다")
            : $"작업을 마쳤습니다{(a.Detail is { } t ? $" ({t} 걸림)" : "")}";
        var folder = a.Project is { } p && Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : null;
        var body = folder is null ? what : $"{what}\n{folder}";
        return new OutboundNotification(
            Guid.NewGuid(),
            a.Kind,
            a.Kind == AgentAlertKind.Waiting ? OutboundPriority.Warning : OutboundPriority.Info,
            a.Title,
            body,
            $"{a.Kind}:{a.App}:{a.Title}:{now.ToUnixTimeMilliseconds()}",
            MessageSensitivity.Normal,
            DeliveryPolicy.EncryptedOnly,
            now,
            ExpiresAt: now.AddHours(a.Kind == AgentAlertKind.Waiting ? 12 : 6),
            Metadata: new Dictionary<string, string> { ["sourceApp"] = a.App });
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().ReplaceLineEndings(" ");
        return t.Length <= max ? t : t[..max];
    }
}
