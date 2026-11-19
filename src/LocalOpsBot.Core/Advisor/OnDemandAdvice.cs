using LocalOpsBot.Core.Localization;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Core.Advisor;

/// <summary>
/// Why advice couldn't be produced, as a stable code the phone app and the tray turn into their own
/// wording (the <c>error</c> of <c>POST /api/v1/advise</c>, DECISIONS M-60). The English detail travels
/// beside it for logs and the Telegram reply.
/// </summary>
public static class AdviceErrors
{
    /// <summary><c>llmAdvisor.enabled</c> is false.</summary>
    public const string Disabled = "disabled";

    /// <summary>No LLM server answered at <c>llmAdvisor.endpoint</c> (Ollama not installed or not running).</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The server is up but <c>llmAdvisor.model</c> isn't pulled.</summary>
    public const string ModelMissing = "model_missing";

    /// <summary>No reply within <c>llmAdvisor.timeoutSeconds</c>.</summary>
    public const string Timeout = "timeout";

    /// <summary>The server answered with another error.</summary>
    public const string ServerError = "server_error";

    /// <summary>The server answered, but with no text.</summary>
    public const string Empty = "empty";

    /// <summary>Anything else.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One piece of on-demand advice: the model's text (or why there is none), the state summary it was
/// given, which model wrote it and when. <see cref="Cached"/>: handed out again within
/// <see cref="OnDemandAdvice.ReuseFor"/> instead of generated for this request.
/// </summary>
public sealed record AdviceReply(
    bool Ok,
    string Text,
    string? Error,
    string? Code,
    string Summary,
    string Model,
    DateTimeOffset GeneratedAt,
    bool Cached = false);

/// <summary>
/// Advice asked for from the phone app or the tray (DECISIONS M-60). Generating it keeps the GPU or CPU
/// busy for seconds, and several phones can ask at once, so: at most one generation runs — callers
/// arriving meanwhile wait for the same one — and a successful reply is handed out again for
/// <see cref="ReuseFor"/>. Failures aren't kept, so "start Ollama, ask again" works at once.
/// Each newly written advice is also kept in the PC's notification center (DECISIONS M-61), as one of
/// this PC's own entries, so it can be read again from the tray later.
/// </summary>
public sealed class OnDemandAdvice
{
    /// <summary>How long a successful reply is handed out again rather than generated anew.</summary>
    public static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(60);

    private readonly IPcStateAdvisor _advisor;
    private readonly LlmAdvisorOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly INotificationHistoryStore? _history;
    private Task<AdviceReply>? _running;
    private AdviceReply? _last;

    public OnDemandAdvice(IPcStateAdvisor advisor, LlmAdvisorOptions options, TimeProvider? time = null,
        INotificationHistoryStore? history = null)
    {
        _advisor = advisor;
        _options = options;
        _time = time ?? TimeProvider.System;
        _history = history;
    }

    /// <summary>
    /// The current advice. <paramref name="ct"/> only stops this caller's wait: the generation itself
    /// goes on (bounded by <c>llmAdvisor.timeoutSeconds</c>) for whoever else is waiting, and its reply
    /// is reused.
    /// </summary>
    public Task<AdviceReply> GetAsync(CancellationToken ct)
    {
        Task<AdviceReply> running;
        lock (_gate)
        {
            // A negative age (the clock was set back) is not "fresh": generate anew.
            if (_last is { } last && _time.GetUtcNow() - last.GeneratedAt is var age && age >= TimeSpan.Zero && age < ReuseFor)
                return Task.FromResult(last with { Cached = true });
            running = _running ??= GenerateAsync();
        }
        return running.WaitAsync(ct);
    }

    private async Task<AdviceReply> GenerateAsync()
    {
        // Leave the lock before any work: GetAsync assigns this task to _running under it.
        await Task.Yield();
        AdviceReply reply;
        try
        {
            var r = await _advisor.AdviseAsync(CancellationToken.None);
            reply = new AdviceReply(r.Ok, r.Text, r.Error, r.Ok ? null : r.Code ?? AdviceErrors.Failed,
                r.Summary, _options.Model, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            reply = new AdviceReply(false, "", ex.Message, AdviceErrors.Failed, "", _options.Model, _time.GetUtcNow());
        }
        lock (_gate)
        {
            if (reply.Ok) _last = reply;
            _running = null;
        }
        if (reply.Ok) await RecordAsync(reply);
        return reply;
    }

    /// <summary>
    /// The notification-center entry for newly written advice: this PC's own (<c>self</c>), the app
    /// "AI 조언", the advice's first line as the title and the whole advice as the body.
    /// </summary>
    public static DeviceNotificationRecord HistoryRecord(AdviceReply reply, string pcName, DateTimeOffset now)
    {
        var firstLine = reply.Text.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•').Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var title = firstLine.Length <= 120 ? firstLine : firstLine[..119] + "…";
        return new DeviceNotificationRecord(
            $"advice-{reply.GeneratedAt.ToUnixTimeMilliseconds()}", NotificationInbox.SelfOrigin, pcName, DeviceKind.Pc,
            Strings.AiAdvisorTitle, title, reply.Text, reply.GeneratedAt, now);
    }

    // A failed write never costs the asker the advice: the history is a convenience.
    private async Task RecordAsync(AdviceReply reply)
    {
        if (_history is null) return;
        try
        {
            await _history.AddAsync(HistoryRecord(reply, Environment.MachineName, _time.GetUtcNow()), CancellationToken.None);
        }
        catch
        {
            // Core has no logger; the advice itself has already been produced.
        }
    }
}
