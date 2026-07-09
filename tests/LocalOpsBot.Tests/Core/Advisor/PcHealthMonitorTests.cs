using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Core.Advisor;

public sealed class PcHealthMonitorTests
{
    private const string CooldownKey = "advisor.last_advised_at";

    // A metrics collector reporting the given CPU load; memory is kept low so only CPU can breach.
    private static FakeSystemMetricsCollector Cpu(double percent) => new()
    {
        NextResult = CollectorResult<SystemMetricSnapshot>.Ok(
            new SystemMetricSnapshot(DateTimeOffset.UtcNow, percent, 16_000_000_000, 12_000_000_000, 25,
                TimeSpan.Zero, "PC", null),
            DateTimeOffset.UtcNow)
    };

    private static PcHealthMonitor Monitor(
        FakeSystemMetricsCollector metrics, AdvisorAlertOptions options,
        FakePcStateAdvisor advisor, FakeAlertDispatcher dispatcher, FakeStateStore state) =>
        new(metrics, new FakeDiskCollector(), new FakeTemperatureCollector(),
            advisor, dispatcher, state, options);

    [Fact]
    public async Task Disabled_does_nothing()
    {
        var advisor = new FakePcStateAdvisor();
        var dispatcher = new FakeAlertDispatcher();
        var monitor = Monitor(Cpu(99), new AdvisorAlertOptions { Enabled = false },
            advisor, dispatcher, new FakeStateStore());

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.Disabled, result.Outcome);
        Assert.Equal(0, advisor.AdviseCallCount);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public async Task No_breach_returns_no_breach_without_advising()
    {
        var advisor = new FakePcStateAdvisor();
        var monitor = Monitor(Cpu(20), new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 1 },
            advisor, new FakeAlertDispatcher(), new FakeStateStore());

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.NoBreach, result.Outcome);
        Assert.Equal(0, advisor.AdviseCallCount);
    }

    [Fact]
    public async Task Breach_advises_and_dispatches_with_trigger_and_cooldown()
    {
        var advisor = new FakePcStateAdvisor { NextResult = new(true, "Close heavy apps.", null) };
        var dispatcher = new FakeAlertDispatcher();
        var state = new FakeStateStore();
        var monitor = Monitor(Cpu(99), new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 1 },
            advisor, dispatcher, state);

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.Advised, result.Outcome);
        Assert.Equal(1, advisor.AdviseCallCount);
        var alert = Assert.Single(dispatcher.Dispatched);
        Assert.Equal("advisor:health", alert.DedupKey);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("Close heavy apps.", alert.Body);
        Assert.Contains("CPU 99%", alert.Body); // trigger line
        Assert.NotNull(await state.GetAsync(CooldownKey, CancellationToken.None));
    }

    [Fact]
    public async Task Long_advice_is_truncated_before_dispatch()
    {
        var advisor = new FakePcStateAdvisor { NextResult = new(true, new string('x', 5000), null) };
        var dispatcher = new FakeAlertDispatcher();
        var monitor = Monitor(Cpu(99), new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 1 },
            advisor, dispatcher, new FakeStateStore());

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.Advised, result.Outcome);
        var alert = Assert.Single(dispatcher.Dispatched);
        Assert.True(alert.Body.Length < 4096, $"body was {alert.Body.Length} chars");
        Assert.EndsWith("…", alert.Body);
    }

    [Fact]
    public async Task Single_breach_below_streak_does_not_advise()
    {
        var advisor = new FakePcStateAdvisor();
        var monitor = Monitor(Cpu(99), new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 2 },
            advisor, new FakeAlertDispatcher(), new FakeStateStore());

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.BelowStreak, result.Outcome);
        Assert.Equal(0, advisor.AdviseCallCount);
    }

    [Fact]
    public async Task Sustained_breach_advises_on_second_poll()
    {
        var advisor = new FakePcStateAdvisor();
        var monitor = Monitor(Cpu(99), new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 2 },
            advisor, new FakeAlertDispatcher(), new FakeStateStore());

        var first = await monitor.PollOnceAsync(CancellationToken.None);
        var second = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.BelowStreak, first.Outcome);
        Assert.Equal(AdviseOutcome.Advised, second.Outcome);
        Assert.Equal(1, advisor.AdviseCallCount);
    }

    [Fact]
    public async Task Streak_resets_when_breach_clears()
    {
        var advisor = new FakePcStateAdvisor();
        var metrics = Cpu(99);
        var monitor = Monitor(metrics, new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 2 },
            advisor, new FakeAlertDispatcher(), new FakeStateStore());

        await monitor.PollOnceAsync(CancellationToken.None); // streak 1 (breach)
        metrics.NextResult = Cpu(10).NextResult;             // now healthy -> resets streak
        var healthy = await monitor.PollOnceAsync(CancellationToken.None);
        metrics.NextResult = Cpu(99).NextResult;             // breach again -> streak 1
        var again = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.NoBreach, healthy.Outcome);
        Assert.Equal(AdviseOutcome.BelowStreak, again.Outcome);
        Assert.Equal(0, advisor.AdviseCallCount);
    }

    [Fact]
    public async Task Within_cooldown_suppresses_advice()
    {
        var advisor = new FakePcStateAdvisor();
        var state = new FakeStateStore();
        await state.SetAsync(CooldownKey, DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None);
        var monitor = Monitor(Cpu(99),
            new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 1, CooldownMinutes = 60 },
            advisor, new FakeAlertDispatcher(), state);

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.InCooldown, result.Outcome);
        Assert.Equal(0, advisor.AdviseCallCount);
    }

    [Fact]
    public async Task After_cooldown_elapsed_advises_again()
    {
        var advisor = new FakePcStateAdvisor();
        var state = new FakeStateStore();
        await state.SetAsync(CooldownKey, DateTimeOffset.UtcNow.AddHours(-2).ToString("O"), CancellationToken.None);
        var monitor = Monitor(Cpu(99),
            new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 1, CooldownMinutes = 60 },
            advisor, new FakeAlertDispatcher(), state);

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.Advised, result.Outcome);
    }

    [Fact]
    public async Task Advice_failure_does_not_dispatch_or_set_cooldown()
    {
        var advisor = new FakePcStateAdvisor { NextResult = new(false, "", "Ollama unreachable") };
        var dispatcher = new FakeAlertDispatcher();
        var state = new FakeStateStore();
        var monitor = Monitor(Cpu(99), new AdvisorAlertOptions { Enabled = true, ConsecutiveBreaches = 1 },
            advisor, dispatcher, state);

        var result = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(AdviseOutcome.AdviceFailed, result.Outcome);
        Assert.Empty(dispatcher.Dispatched);
        Assert.Null(await state.GetAsync(CooldownKey, CancellationToken.None));
    }
}
