using System.Globalization;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Advisor;

/// <summary>What a single monitor poll decided (also handy for logging and tests).</summary>
public enum AdviseOutcome
{
    Disabled,
    NoBreach,
    BelowStreak,
    InCooldown,
    AdviceFailed,
    Advised
}

/// <summary>Outcome of one poll, with the breaches seen and an optional human-readable detail.</summary>
public sealed record PollResult(AdviseOutcome Outcome, IReadOnlyList<HealthBreach> Breaches, string? Detail);

/// <summary>
/// Active health advisor: one poll samples the machine, evaluates thresholds, and — when a breach
/// persists (sustained streak) and the cooldown has elapsed — asks the LLM for advice and
/// dispatches it as an alert. All spam control lives here so the LLM is not called needlessly; the
/// alert dispatcher's mute/dedup/rate-limit stay a final backstop. Held as a singleton because the
/// breach streak is in-memory state across polls.
/// </summary>
public sealed class PcHealthMonitor
{
    private const string LastAdvisedKey = "advisor.last_advised_at";

    private readonly ISystemMetricsCollector _metrics;
    private readonly IDiskCollector _disk;
    private readonly ITemperatureCollector _temperature;
    private readonly IPcStateAdvisor _advisor;
    private readonly IAlertDispatcher _dispatcher;
    private readonly IStateStore _state;
    private readonly AdvisorAlertOptions _options;
    private readonly string _machineName = Environment.MachineName;

    private int _breachStreak;

    public PcHealthMonitor(
        ISystemMetricsCollector metrics,
        IDiskCollector disk,
        ITemperatureCollector temperature,
        IPcStateAdvisor advisor,
        IAlertDispatcher dispatcher,
        IStateStore state,
        AdvisorAlertOptions options)
    {
        _metrics = metrics;
        _disk = disk;
        _temperature = temperature;
        _advisor = advisor;
        _dispatcher = dispatcher;
        _state = state;
        _options = options;
    }

    public async Task<PollResult> PollOnceAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
            return new PollResult(AdviseOutcome.Disabled, [], null);

        var metrics = (await _metrics.CollectAsync(ct)).Snapshot;
        var disks = (await _disk.CollectAsync(ct)).Snapshot;
        var temps = (await _temperature.CollectAsync(ct)).Snapshot;

        var breaches = HealthThresholdEvaluator.Evaluate(metrics, disks, temps, _options);
        if (breaches.Count == 0)
        {
            _breachStreak = 0;
            return new PollResult(AdviseOutcome.NoBreach, breaches, null);
        }

        var needed = Math.Max(1, _options.ConsecutiveBreaches);
        _breachStreak = Math.Min(_breachStreak + 1, needed);
        if (_breachStreak < needed)
            return new PollResult(AdviseOutcome.BelowStreak, breaches, $"streak {_breachStreak}/{needed}");

        if (await InCooldownAsync(ct))
            return new PollResult(AdviseOutcome.InCooldown, breaches, null);

        var advice = await _advisor.AdviseAsync(ct);
        if (!advice.Ok)
            return new PollResult(AdviseOutcome.AdviceFailed, breaches, advice.Error);

        var trigger = string.Join("; ", breaches.Select(b => b.Detail));
        var body = $"Triggered by: {trigger}\n\n{advice.Text}";
        await _dispatcher.DispatchAsync(new AlertEvent(
            Guid.NewGuid().ToString("N"), "advisor", AlertSeverity.Warning,
            "PC health advice", body, "advisor:health", _machineName, DateTimeOffset.UtcNow), ct);

        // Record the cooldown only after a successful dispatch, so a send failure lets it retry.
        await _state.SetAsync(LastAdvisedKey, DateTimeOffset.UtcNow.ToString("O"), ct);
        return new PollResult(AdviseOutcome.Advised, breaches, trigger);
    }

    private async Task<bool> InCooldownAsync(CancellationToken ct)
    {
        var raw = await _state.GetAsync(LastAdvisedKey, ct);
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
            return DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(_options.CooldownMinutes);
        return false;
    }
}
