using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Tests.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Core.Advisor;

/// <summary>
/// Advice asked for from the phone app or the tray (DECISIONS M-60): one generation at a time, shared
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
    public async Task A_clock_set_back_does_not_keep_old_advice()
    {
        var time = new ManualTimeProvider();
        var advisor = new GatedAdvisor { Immediate = new AdvisoryResult(true, "All fine.", null) };
        var advice = new OnDemandAdvice(advisor, Options, time);

        await advice.GetAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(-10));
        var after = await advice.GetAsync(CancellationToken.None);

        Assert.False(after.Cached);
        Assert.Equal(2, advisor.Calls);
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

    // ---- kept in the PC's notification center (M-61) -------------------------------------------

    [Fact]
    public async Task New_advice_is_kept_in_the_notification_center_once()
    {
        var time = new ManualTimeProvider();
        var history = new RecordingStore();
        var advisor = new GatedAdvisor { Immediate = new AdvisoryResult(true, "- CPU is busy: close Chrome.\n- Free up C:.", null) };
        var advice = new OnDemandAdvice(advisor, Options, time, history);

        await advice.GetAsync(CancellationToken.None);
        await advice.GetAsync(CancellationToken.None); // reused within the minute: not kept again
        await history.WaitForAsync(1); // written off the asker's path

        var r = Assert.Single(history.Records);
        Assert.Equal(NotificationInbox.SelfOrigin, r.OriginId);
        Assert.Equal(DeviceKind.Pc, r.OriginKind);
        Assert.Equal("CPU is busy: close Chrome.", r.Title);
        Assert.Equal("- CPU is busy: close Chrome.\n- Free up C:.", r.Body);
        Assert.Equal($"advice-{time.Now.ToUnixTimeMilliseconds()}", r.NotifId);
    }

    [Fact]
    public async Task A_failure_is_not_kept_in_the_notification_center()
    {
        var history = new RecordingStore();
        var advisor = new GatedAdvisor { Immediate = new AdvisoryResult(false, "", "down", AdviceErrors.Unreachable) };
        await new OnDemandAdvice(advisor, Options, new ManualTimeProvider(), history).GetAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task A_history_that_fails_to_write_does_not_cost_the_advice()
    {
        var advisor = new GatedAdvisor { Immediate = new AdvisoryResult(true, "Fine.", null) };
        var reply = await new OnDemandAdvice(advisor, Options, new ManualTimeProvider(), new RecordingStore { Fail = true })
            .GetAsync(CancellationToken.None);

        Assert.True(reply.Ok);
        Assert.Equal("Fine.", reply.Text);
    }

    [Fact]
    public void A_long_first_line_is_cut_for_the_title()
    {
        var reply = new AdviceReply(true, new string('a', 300), null, null, "", "m", DateTimeOffset.UnixEpoch);
        var r = OnDemandAdvice.HistoryRecord(reply, "HOME-PC", DateTimeOffset.UnixEpoch);

        Assert.Equal(120, r.Title.Length);
        Assert.EndsWith("…", r.Title);
        Assert.Equal("HOME-PC", r.OriginName);
    }

    [Theory]
    [InlineData("1. **CPU** is pegged.\n2. Disk is full.", "CPU is pegged.")]
    [InlineData("\n  - Close Chrome.", "Close Chrome.")]
    [InlineData("• Fine.", "Fine.")]
    [InlineData("2) __Restart__ the PC.", "Restart the PC.")]
    public void The_title_drops_list_markers_and_emphasis(string text, string title) =>
        Assert.Equal(title, OnDemandAdvice.HistoryRecord(new AdviceReply(true, text, null, null, "", "m", DateTimeOffset.UnixEpoch),
            "PC", DateTimeOffset.UnixEpoch).Title);

    private sealed class RecordingStore : INotificationHistoryStore
    {
        public List<DeviceNotificationRecord> Records { get; } = new();
        public bool Fail { get; init; }

        public Task AddAsync(DeviceNotificationRecord record, CancellationToken ct)
        {
            if (Fail) throw new IOException("disk full");
            lock (Records) Records.Add(record);
            return Task.CompletedTask;
        }

        public async Task WaitForAsync(int count)
        {
            for (var i = 0; i < 200; i++)
            {
                lock (Records) if (Records.Count >= count) return;
                await Task.Delay(10);
            }
        }

        public Task<HistoryPage> ListAsync(HistoryCursor? after, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ClearDevicesAsync(LocalOpsBot.Core.Notifications.HistoryClearScope? scope, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteDeviceAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> PruneAsync(DateTimeOffset cutoff, int maxDeviceEntries, CancellationToken ct) => throw new NotSupportedException();
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
