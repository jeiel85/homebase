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
/// </summary>
public sealed class OnDemandAdvice
{
    /// <summary>How long a successful reply is handed out again rather than generated anew.</summary>
    public static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(60);

    private readonly IPcStateAdvisor _advisor;
    private readonly LlmAdvisorOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task<AdviceReply>? _running;
    private AdviceReply? _last;

    public OnDemandAdvice(IPcStateAdvisor advisor, LlmAdvisorOptions options, TimeProvider? time = null)
    {
        _advisor = advisor;
        _options = options;
        _time = time ?? TimeProvider.System;
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
        return reply;
    }
}
