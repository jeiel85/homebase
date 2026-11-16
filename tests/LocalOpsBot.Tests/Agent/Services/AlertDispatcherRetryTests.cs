using LocalOpsBot.Agent.Services;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Tests.Fakes;
using LocalOpsBot.Tests.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Agent.Services;

/// <summary>Re-sending alerts that failed during a short network outage (#2).</summary>
public sealed class AlertDispatcherRetryTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeAlertStore _store = new();
    private readonly ScriptedRouter _router = new();
    private readonly AlertDispatcher _dispatcher;

    public AlertDispatcherRetryTests()
    {
        _dispatcher = new AlertDispatcher(new AllowAll(), _store, _router, NullLogger<AlertDispatcher>.Instance, _time);
    }

    private static OutboundDeliveryResult Ok() =>
        new(true, [new OutboundAttemptResult(ChannelIds.Telegram, true)]);

    private static OutboundDeliveryResult Fail(OutboundFailureKind kind) =>
        new(false, [new OutboundAttemptResult(ChannelIds.Telegram, false, kind, kind.ToString())]);

    private static AlertEvent Alert(string dedupKey = "k1") => new(
        Guid.NewGuid().ToString("N"), "event_log", AlertSeverity.Warning, "Disk error", "Body", dedupKey, "host",
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_transient_failure_is_re_sent_after_the_backoff_and_marked_sent()
    {
        var alert = Alert();
        _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
        await _dispatcher.DispatchAsync(alert, default);

        Assert.Equal(1, _dispatcher.PendingRetries);
        Assert.Equal("Failed", Assert.Single(await _store.GetRecentAsync(10, default)).Status);

        await _dispatcher.RetryDueAsync(default); // not due yet
        Assert.Single(_router.Sent);

        _time.Advance(AlertDispatcher.RetryDelays[0]);
        _router.Results.Enqueue(Ok());
        await _dispatcher.RetryDueAsync(default);

        Assert.Equal(2, _router.Sent.Count);
        // The same notification id: a phone that got both shows it once.
        Assert.Equal(_router.Sent[0].NotificationId, _router.Sent[1].NotificationId);
        Assert.Equal(0, _dispatcher.PendingRetries);
        var row = Assert.Single(await _store.GetRecentAsync(10, default));
        Assert.Equal(alert.AlertId, row.AlertId);
        Assert.Equal("Sent", row.Status);
    }

    [Theory]
    [InlineData(OutboundFailureKind.NotConfigured)]
    [InlineData(OutboundFailureKind.Unauthorized)]
    [InlineData(OutboundFailureKind.Permanent)]
    [InlineData(OutboundFailureKind.Expired)]
    public async Task Failures_only_configuration_can_fix_are_not_re_sent(OutboundFailureKind kind)
    {
        _router.Results.Enqueue(Fail(kind));

        await _dispatcher.DispatchAsync(Alert(), default);

        Assert.Equal(0, _dispatcher.PendingRetries);
    }

    [Theory]
    [InlineData(OutboundFailureKind.Transient)]
    [InlineData(OutboundFailureKind.RateLimited)]
    [InlineData(OutboundFailureKind.EndpointOffline)]
    [InlineData(OutboundFailureKind.Unknown)]
    public void Passing_failures_are_retryable(OutboundFailureKind kind) =>
        Assert.True(AlertDispatcher.IsRetryable(Fail(kind)));

    [Fact]
    public async Task A_later_delivery_with_the_same_dedup_key_cancels_the_re_send()
    {
        _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
        await _dispatcher.DispatchAsync(Alert("same"), default);
        _router.Results.Enqueue(Ok());
        await _dispatcher.DispatchAsync(Alert("same"), default);

        Assert.Equal(0, _dispatcher.PendingRetries);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _dispatcher.RetryDueAsync(default);
        Assert.Equal(2, _router.Sent.Count); // nothing went out a second time
    }

    [Fact]
    public async Task Only_one_re_send_per_dedup_key_waits()
    {
        _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
        _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
        await _dispatcher.DispatchAsync(Alert("same"), default);
        await _dispatcher.DispatchAsync(Alert("same"), default);

        Assert.Equal(1, _dispatcher.PendingRetries);
    }

    [Fact]
    public async Task A_delivery_makes_the_waiting_re_sends_due_at_once()
    {
        _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
        await _dispatcher.DispatchAsync(Alert("a"), default);
        _router.Results.Enqueue(Ok());
        await _dispatcher.DispatchAsync(Alert("b"), default); // the network is back

        _router.Results.Enqueue(Ok());
        await _dispatcher.RetryDueAsync(default); // no time has passed

        Assert.Equal(3, _router.Sent.Count);
        Assert.Equal(0, _dispatcher.PendingRetries);
    }

    [Fact]
    public async Task Re_sending_stops_after_the_last_backoff()
    {
        _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
        await _dispatcher.DispatchAsync(Alert(), default);

        foreach (var delay in AlertDispatcher.RetryDelays)
        {
            _time.Advance(delay);
            _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
            await _dispatcher.RetryDueAsync(default);
        }

        Assert.Equal(1 + AlertDispatcher.RetryDelays.Length, _router.Sent.Count);
        Assert.Equal(0, _dispatcher.PendingRetries);
        Assert.Equal("Failed", Assert.Single(await _store.GetRecentAsync(10, default)).Status);
    }

    [Fact]
    public async Task An_alert_older_than_the_limit_is_dropped_without_sending()
    {
        _router.Results.Enqueue(Fail(OutboundFailureKind.Transient));
        await _dispatcher.DispatchAsync(Alert(), default);

        _time.Advance(AlertDispatcher.MaxRetryAge + TimeSpan.FromMinutes(1)); // e.g. the PC slept
        await _dispatcher.RetryDueAsync(default);

        Assert.Single(_router.Sent);
        Assert.Equal(0, _dispatcher.PendingRetries);
    }

    private sealed class AllowAll : IAlertPolicy
    {
        public Task<AlertDecision> ShouldSendAsync(AlertEvent alert, CancellationToken ct) =>
            Task.FromResult(new AlertDecision(true, null));
    }

    private sealed class ScriptedRouter : IOutboundRouter
    {
        public Queue<OutboundDeliveryResult> Results { get; } = new();
        public List<OutboundNotification> Sent { get; } = new();

        public Task<OutboundDeliveryResult> DeliverAsync(OutboundNotification notification, CancellationToken ct)
        {
            Sent.Add(notification);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Ok());
        }
    }
}
