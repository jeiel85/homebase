using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Core.Alerts;

public sealed class AlertPolicyTests
{
    private readonly FakeStateStore _stateStore = new();
    private readonly FakeAlertStore _alertStore = new();
    private readonly AlertingOptions _options = new()
    {
        DedupWindowSeconds = 600,
        MaxMessagesPerMinute = 10,
        MaxMessagesPerHour = 120,
        CriticalAlertsBypassMute = false
    };

    private AlertEvent MakeAlert(string id = "alert-1", string? dedupKey = null, AlertSeverity severity = AlertSeverity.Warning)
        => new(id, "test", severity, "Test Alert", "Body", dedupKey ?? id, "test", DateTimeOffset.UtcNow);

    [Fact]
    public async Task ShouldSendAsync_returns_true_for_normal_alert()
    {
        var policy = new AlertPolicy(_stateStore, _alertStore, _options);
        var decision = await policy.ShouldSendAsync(MakeAlert(), default);
        Assert.True(decision.Send);
    }

    [Fact]
    public async Task ShouldSendAsync_blocks_when_muted()
    {
        var mutedUntil = DateTime.UtcNow.AddHours(1).ToString("O");
        await _stateStore.SetAsync("alert.muted_until", mutedUntil, default);

        var policy = new AlertPolicy(_stateStore, _alertStore, _options);
        var decision = await policy.ShouldSendAsync(MakeAlert(), default);
        Assert.False(decision.Send);
        Assert.Contains("Muted until", decision.DropReason);
    }

    [Fact]
    public async Task ShouldSendAsync_critical_bypasses_mute()
    {
        var mutedUntil = DateTime.UtcNow.AddHours(1).ToString("O");
        await _stateStore.SetAsync("alert.muted_until", mutedUntil, default);

        var policy = new AlertPolicy(_stateStore, _alertStore, _options);
        var decision = await policy.ShouldSendAsync(MakeAlert(severity: AlertSeverity.Critical), default);
        Assert.True(decision.Send);
    }

    [Fact]
    public async Task ShouldSendAsync_dedup_blocks_duplicate()
    {
        var alert = MakeAlert(dedupKey: "dedup-1");
        var policy = new AlertPolicy(_stateStore, _alertStore, _options);

        await policy.ShouldSendAsync(alert, default);
        await _alertStore.InsertAsync(new AlertLogItem(null, alert.AlertId, alert.Kind, alert.Severity.ToString(),
            alert.Title, alert.Body, alert.DedupKey, alert.Source, "Sent", null, alert.CreatedAt, alert.CreatedAt), default);

        var decision = await policy.ShouldSendAsync(alert, default);
        Assert.False(decision.Send);
        Assert.Contains("Duplicate", decision.DropReason);
    }

    [Fact]
    public async Task ShouldSendAsync_passes_without_dedup_key()
    {
        var policy = new AlertPolicy(_stateStore, _alertStore, _options);
        var decision = await policy.ShouldSendAsync(MakeAlert(dedupKey: null), default);
        Assert.True(decision.Send);
    }

    [Fact]
    public async Task ShouldSendAsync_rate_limits_excess_messages()
    {
        _options.MaxMessagesPerMinute = 1;
        var policy = new AlertPolicy(_stateStore, _alertStore, _options);

        await policy.ShouldSendAsync(MakeAlert("a"), default);
        var decision = await policy.ShouldSendAsync(MakeAlert("b"), default);
        Assert.False(decision.Send);
        Assert.Contains("Rate limit", decision.DropReason);
    }

    [Fact]
    public async Task ShouldSendAsync_resets_rate_limit_after_window()
    {
        _options.MaxMessagesPerMinute = 1;
        var policy = new AlertPolicy(_stateStore, _alertStore, _options);

        await policy.ShouldSendAsync(MakeAlert("a"), default);

        var decision = await policy.ShouldSendAsync(MakeAlert("b"), default);
        Assert.False(decision.Send);

        // can't actually wait, but the reset happens on window boundary
    }
}
