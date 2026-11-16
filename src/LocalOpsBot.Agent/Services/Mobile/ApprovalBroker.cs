using System.Collections.Concurrent;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>Result of <see cref="ApprovalBroker.Answer"/>.</summary>
public enum ApprovalAnswerOutcome { Accepted, Unknown, Invalid }

/// <summary>
/// Claude Code permission prompts waiting for a phone (DECISIONS M-48). The Tray hands one over
/// (bridge op <c>approval.ask</c>) and waits; the broker pushes it and returns the first phone's
/// answer (<c>POST /api/v1/approval</c>) — or null, meaning "ask on the PC as usual", when nothing
/// was delivered, the deadline passes, the Tray gives up (the user came back) or too many are open.
/// Only memory: a restarted Agent forgets every prompt, and their hooks fall back to the PC.
/// </summary>
public sealed class ApprovalBroker
{
    public const int MaxPending = 20;

    /// <summary>A session's "waiting" alert (M-45) is held back this long after its approval push.</summary>
    public static readonly TimeSpan WaitingAlertQuiet = TimeSpan.FromMinutes(2);

    private sealed record Pending(ApprovalRequest Request, TaskCompletionSource<string?> Answer);

    private readonly IOutboundRouter? _router;
    private readonly TimeProvider _time;
    private readonly ILogger<ApprovalBroker> _logger;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _askedSessions = new(StringComparer.Ordinal);

    public ApprovalBroker(ILogger<ApprovalBroker> logger, IOutboundRouter? router = null, TimeProvider? time = null)
    {
        _router = router;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public int PendingCount => _pending.Count;

    /// <summary>Pushes <paramref name="request"/> and waits for a phone; null means "ask on the PC".</summary>
    public async Task<string?> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (_router is null) return null;
        var now = _time.GetUtcNow();
        var deadline = request.ExpiresAt < now + ClaudeHook.WaitLimit ? request.ExpiresAt : now + ClaudeHook.WaitLimit;
        if (deadline <= now) return null;
        if (_pending.Count >= MaxPending)
        {
            _logger.LogWarning("Claude approval not sent: {Count} are already waiting for a phone", _pending.Count);
            return null;
        }

        var pending = new Pending(request with { ExpiresAt = deadline }, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_pending.TryAdd(request.Id, pending)) return null;
        // The deadline runs from now, delivery included: a slow push must not lengthen the window the
        // phone was told about (ExpiresAt), and an answer after it is refused in Answer.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(deadline - now);
        try
        {
            _askedSessions[SessionKey(request.App, request.Session)] = now;
            var result = await _router.DeliverAsync(ClaudeHook.ToNotification(pending.Request, now), limit.Token);
            if (!result.Delivered)
            {
                _logger.LogInformation("Claude approval could not reach a phone ({Error}); asking on the PC", result.CombinedError);
                return null;
            }
            _logger.LogInformation("Claude approval pushed: {Tool} in {Session}", request.ToolTitle, request.Session);

            await using (limit.Token.Register(() => pending.Answer.TrySetResult(null)))
            {
                var decision = await pending.Answer.Task;
                _logger.LogInformation("Claude approval {Id}: {Decision}", request.Id, decision ?? "no answer");
                return decision;
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Claude approval {Id} ran out of time while being delivered; asking on the PC", request.Id);
            return null;
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    /// <summary>A phone's answer. The first answer wins; later ones and unknown ids are <see cref="ApprovalAnswerOutcome.Unknown"/>.</summary>
    public ApprovalAnswerOutcome Answer(string? id, string? decision, string deviceName)
    {
        if (string.IsNullOrEmpty(id) || !ApprovalDecision.IsKnown(decision)) return ApprovalAnswerOutcome.Invalid;
        if (!_pending.TryGetValue(id, out var p)) return ApprovalAnswerOutcome.Unknown;
        // Past the advertised deadline the prompt is the PC's again, even if its timer has not fired yet.
        if (_time.GetUtcNow() >= p.Request.ExpiresAt) return ApprovalAnswerOutcome.Unknown;
        if (!_pending.TryRemove(id, out _) || !p.Answer.TrySetResult(decision)) return ApprovalAnswerOutcome.Unknown;
        _logger.LogInformation("Claude approval {Id} answered {Decision} from {Device}", id, decision, deviceName);
        return ApprovalAnswerOutcome.Accepted;
    }

    /// <summary>The Tray no longer waits (the user is back at the PC): the prompt is withdrawn.</summary>
    public void Cancel(string? id)
    {
        if (id is not null && _pending.TryRemove(id, out var p)) p.Answer.TrySetResult(null);
    }

    /// <summary>
    /// Whether the session's "waiting" alert (M-45) should be held back: its prompt was just pushed
    /// with Allow / Deny, which says the same thing more usefully.
    /// </summary>
    public bool RecentlyAsked(string app, string session)
    {
        var now = _time.GetUtcNow();
        foreach (var old in _askedSessions.Where(kv => now - kv.Value > WaitingAlertQuiet).Select(kv => kv.Key).ToList())
            _askedSessions.TryRemove(old, out _);
        return _askedSessions.TryGetValue(SessionKey(app, session), out var at) && now - at <= WaitingAlertQuiet;
    }

    private static string SessionKey(string app, string session) => app + "|" + session;
}
