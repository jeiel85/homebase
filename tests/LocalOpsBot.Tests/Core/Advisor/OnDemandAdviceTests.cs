using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tests.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Core.Advisor;

/// <summary>
/// Advice asked for from the phone app or the tray (DECISIONS M-59): one generation at a time, shared
/// by everyone waiting, a successful reply reused for a minute, a failure never kept.
/// </summary>
public sealed class OnDemandAdviceTests
{
    private static readonly LlmAdvisorOptions Options = new() { Model = "llama3.2:1b" };

    [Fact]
    public async Task Callers_arriving_while_it_runs_share_one_generation()
    {
        var advisor = new GatedAdvisor();
        var advice = new OnDemandAdvice(advisor, Options, new ManualTimeProvider());

        var first = advice.GetAsync(CancellationToken.None);
        var second = advice.GetAsync(CancellationToken.None);
        await advisor.Started.Task;
        advisor.Finish(new AdvisoryResult(true, "Close Chrome.", null, null, "CPU load: 92%"));

        var a = await first;
        var b = await second;
        Assert.Equal(1, advisor.Calls);
        Assert.Same(a, b);
        Assert.True(a.Ok);
        Assert.Equal("Close Chrome.", a.Text);
        Assert.Equal("CPU load: 92%", a.Summary);
        Assert.Equal("llama3.2:1b", a.Model);
        Assert.False(a.Cached);
    }

    [Fact]
    public async Task A_good_reply_is_reused_for_a_minute()
    {
        var time = new ManualTimeProvider();
        var advisor = new GatedAdvisor { Immediate = new AdvisoryResult(true, "All fine.", null, null, "s") };
        var advice = new OnDemandAdvice(advisor, Options, time);

        var made = await advice.GetAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(59));
        var reused = await advice.GetAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        var fresh = await advice.GetAsync(CancellationToken.None);

        Assert.Equal(2, advisor.Calls);
        Assert.True(reused.Cached);
        Assert.Equal(made.GeneratedAt, reused.GeneratedAt);
        Assert.False(fresh.Cached);
        Assert.True(fresh.GeneratedAt > made.GeneratedAt);
    }

    [Fact]
    public async Task A_failure_is_not_kept_so_asking_again_tries_again()
    {
        var advisor = new GatedAdvisor
        {
            Immediate = new AdvisoryResult(false, "", "Could not reach a local LLM.", AdviceErrors.Unreachable),
        };
        var advice = new OnDemandAdvice(advisor, Options, new ManualTimeProvider());

        var failed = await advice.GetAsync(CancellationToken.None);
        advisor.Immediate = new AdvisoryResult(true, "All fine.", null);
        var ok = await advice.GetAsync(CancellationToken.None);

        Assert.False(failed.Ok);
        Assert.Equal(AdviceErrors.Unreachable, failed.Code);
        Assert.True(ok.Ok);
        Assert.Equal(2, advisor.Calls);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_cancel_the_others()
    {
        var advisor = new GatedAdvisor();
        var advice = new OnDemandAdvice(advisor, Options, new ManualTimeProvider());
        using var impatient = new CancellationTokenSource();

        var leaving = advice.GetAsync(impatient.Token);
        var staying = advice.GetAsync(CancellationToken.None);
        await advisor.Started.Task;
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        advisor.Finish(new AdvisoryResult(true, "Restart the PC.", null));

        Assert.Equal("Restart the PC.", (await staying).Text);
        Assert.Equal(1, advisor.Calls);
    }

    [Fact]
    public async Task An_advisor_that_throws_becomes_a_failed_reply()
    {
        var advisor = new GatedAdvisor { Throw = new InvalidOperationException("boom") };
        var reply = await new OnDemandAdvice(advisor, Options, new ManualTimeProvider()).GetAsync(CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Equal(AdviceErrors.Failed, reply.Code);
        Assert.Equal("boom", reply.Error);
    }

    [Fact]
    public async Task A_failure_without_a_code_is_reported_as_failed()
    {
        var advisor = new GatedAdvisor { Immediate = new AdvisoryResult(false, "", "odd") };
        var reply = await new OnDemandAdvice(advisor, Options, new ManualTimeProvider()).GetAsync(CancellationToken.None);

        Assert.Equal(AdviceErrors.Failed, reply.Code);
    }

    [Fact]
    public void The_wire_reply_carries_text_only_when_it_worked()
    {
        var at = new DateTimeOffset(2026, 11, 19, 1, 30, 0, TimeSpan.Zero);
        var ok = AdviceResponse.From(new AdviceReply(true, "Fine.", null, null, "CPU load: 5%", "llama3.2:1b", at, Cached: true));
        var failed = AdviceResponse.From(new AdviceReply(false, "", "not pulled", AdviceErrors.ModelMissing, "", "llama3.2:1b", at));

        Assert.Equal(("Fine.", "CPU load: 5%", true, (string?)null, (string?)null), (ok.Text, ok.Summary, ok.Cached, ok.Error, ok.Message));
        Assert.Null(failed.Text);
        Assert.Null(failed.Summary); // empty → left out
        Assert.Equal(AdviceErrors.ModelMissing, failed.Error);
        Assert.Equal("not pulled", failed.Message);
    }

    /// <summary>An advisor whose reply the test hands over when it chooses (or at once, with <see cref="Immediate"/>).</summary>
    private sealed class GatedAdvisor : IPcStateAdvisor
    {
        private readonly TaskCompletionSource<AdvisoryResult> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AdvisoryResult? Immediate { get; set; }
        public Exception? Throw { get; set; }
        public int Calls => Volatile.Read(ref _calls);

        public void Finish(AdvisoryResult result) => _reply.TrySetResult(result);

        public Task<AdvisoryResult> AdviseAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            if (Throw is not null) throw Throw;
            return Immediate is { } now ? Task.FromResult(now) : _reply.Task;
        }

        public Task<string> BuildStateSummaryAsync(CancellationToken ct) => Task.FromResult("summary");
    }
}
