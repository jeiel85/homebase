using System;
using LocalOpsBot.Core.Monitoring;
using Xunit;

namespace LocalOpsBot.Tests.Core.Monitoring;

public sealed class EventAlertPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private static WindowsEventLogItem Ev(string level, string? provider = "Prov", int eventId = 1000) =>
        new("Application", 1, eventId, provider, level, T0, "PC", "message");

    private static EventLogOptions Opts(string[]? alertLevels = null, int suppress = 60) =>
        new() { AlertLevels = alertLevels ?? new[] { "Critical", "Error" }, RepeatSuppressMinutes = suppress };

    [Fact]
    public void Level_not_in_alert_levels_is_silent()
    {
        var p = new EventAlertPolicy(Opts(alertLevels: new[] { "Critical" }));
        Assert.False(p.ShouldAlert(Ev("Error"), T0));    // Error not in alert levels
        Assert.True(p.ShouldAlert(Ev("Critical"), T0));  // Critical is
    }

    [Fact]
    public void Critical_always_alerts_even_when_repeated()
    {
        var p = new EventAlertPolicy(Opts());
        Assert.True(p.ShouldAlert(Ev("Critical"), T0));
        Assert.True(p.ShouldAlert(Ev("Critical"), T0.AddSeconds(1))); // repeat still alerts
    }

    [Fact]
    public void Repeated_error_is_suppressed_within_window_then_alerts_after()
    {
        var p = new EventAlertPolicy(Opts(suppress: 60));
        Assert.True(p.ShouldAlert(Ev("Error"), T0));                 // first
        Assert.False(p.ShouldAlert(Ev("Error"), T0.AddMinutes(30))); // within 60m — suppressed
        Assert.True(p.ShouldAlert(Ev("Error"), T0.AddMinutes(61)));  // past window — alerts again
    }

    [Fact]
    public void Different_error_sources_are_not_suppressed()
    {
        var p = new EventAlertPolicy(Opts());
        Assert.True(p.ShouldAlert(Ev("Error", provider: "A", eventId: 1), T0));
        Assert.True(p.ShouldAlert(Ev("Error", provider: "B", eventId: 1), T0)); // different provider
        Assert.True(p.ShouldAlert(Ev("Error", provider: "A", eventId: 2), T0)); // different event id
    }

    [Fact]
    public void Zero_suppress_minutes_disables_suppression()
    {
        var p = new EventAlertPolicy(Opts(suppress: 0));
        Assert.True(p.ShouldAlert(Ev("Error"), T0));
        Assert.True(p.ShouldAlert(Ev("Error"), T0)); // no suppression
    }

    [Fact]
    public void Alert_levels_are_case_insensitive()
    {
        var p = new EventAlertPolicy(Opts(alertLevels: new[] { "critical", "error" }));
        Assert.True(p.ShouldAlert(Ev("Critical"), T0));
        Assert.True(p.ShouldAlert(Ev("Error"), T0));
    }
}
