using LocalOpsBot.Agent.Services;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Infrastructure.Telegram;
using LocalOpsBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Agent.Services;

public sealed class AlertDispatcherTests
{
    private readonly FakeStateStore _state = new();
    private readonly FakeAlertStore _store = new();
    private readonly FakeTelegramClient _telegram = new();
    private readonly AlertingOptions _alerting = new() { DedupWindowSeconds = 600, MaxMessagesPerMinute = 10 };

    private AlertDispatcher CreateDispatcher(params long[] chatIds)
    {
        var policy = new AlertPolicy(_state, _store, _alerting);
        var opts = Options.Create(new TelegramOptions { AllowedChatIds = chatIds });
        return new AlertDispatcher(policy, _store, _telegram, opts, NullLogger<AlertDispatcher>.Instance);
    }

    private static AlertEvent MakeAlert(string dedupKey = "k1", AlertSeverity sev = AlertSeverity.Warning)
        => new(Guid.NewGuid().ToString("N"), "test", sev, "Test Title", "Body", dedupKey, "host", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Dispatch_sends_and_records_when_policy_allows()
    {
        var d = CreateDispatcher(123456789);

        await d.DispatchAsync(MakeAlert(), default);

        Assert.Single(_telegram.Sent);
        Assert.Equal(123456789, _telegram.Sent[0].ChatId);
        var recent = await _store.GetRecentAsync(10, default);
        Assert.Single(recent);
        Assert.Equal("Sent", recent[0].Status);
    }

    [Fact]
    public async Task Dispatch_is_suppressed_and_not_recorded_when_muted()
    {
        await _state.SetAsync("alert.muted_until", DateTime.UtcNow.AddHours(1).ToString("O"), default);
        var d = CreateDispatcher(123456789);

        await d.DispatchAsync(MakeAlert(sev: AlertSeverity.Warning), default);

        Assert.Empty(_telegram.Sent);
        Assert.Empty(await _store.GetRecentAsync(10, default));
    }

    [Fact]
    public async Task Dispatch_critical_bypasses_mute()
    {
        await _state.SetAsync("alert.muted_until", DateTime.UtcNow.AddHours(1).ToString("O"), default);
        var d = CreateDispatcher(123456789);

        await d.DispatchAsync(MakeAlert(sev: AlertSeverity.Critical), default);

        Assert.Single(_telegram.Sent);
    }

    [Fact]
    public async Task Dispatch_deduplicates_repeated_alert_within_window()
    {
        var d = CreateDispatcher(123456789);

        await d.DispatchAsync(MakeAlert(dedupKey: "same-key"), default);
        await d.DispatchAsync(MakeAlert(dedupKey: "same-key"), default);

        Assert.Single(_telegram.Sent); // second is deduped
    }

    [Fact]
    public async Task Dispatch_skips_when_no_allowed_chat()
    {
        var d = CreateDispatcher(); // empty allowlist

        await d.DispatchAsync(MakeAlert(), default);

        Assert.Empty(_telegram.Sent);
        Assert.Empty(await _store.GetRecentAsync(10, default));
    }
}
