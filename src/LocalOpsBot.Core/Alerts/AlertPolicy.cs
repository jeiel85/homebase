namespace LocalOpsBot.Core.Alerts;

public sealed class AlertPolicy : IAlertPolicy
{
    private readonly IStateStore _stateStore;
    private readonly IAlertStore _alertStore;
    private readonly AlertingOptions _options;
    private int _messageCount = 0;
    private DateTime _windowStart = DateTime.UtcNow;

    private const string MutedUntilKey = "alert.muted_until";

    public AlertPolicy(
        IStateStore stateStore,
        IAlertStore alertStore,
        AlertingOptions options)
    {
        _stateStore = stateStore;
        _alertStore = alertStore;
        _options = options;
    }

    public async Task<AlertDecision> ShouldSendAsync(AlertEvent alert, CancellationToken ct)
    {
        if (alert.Severity != AlertSeverity.Critical)
        {
            var mutedUntilStr = await _stateStore.GetAsync(MutedUntilKey, ct);
            if (DateTime.TryParse(mutedUntilStr, out var mutedUntil) && DateTime.UtcNow < mutedUntil)
                return new AlertDecision(false, $"Muted until {mutedUntil:HH:mm} UTC");
        }

        if (!string.IsNullOrEmpty(alert.DedupKey))
        {
            var exists = await _alertStore.ExistsRecentDedupKeyAsync(alert.DedupKey, TimeSpan.FromSeconds(_options.DedupWindowSeconds), ct);
            if (exists)
                return new AlertDecision(false, "Duplicate within dedup window");
        }

        ResetWindowIfNeeded();
        if (_messageCount >= _options.MaxMessagesPerMinute)
        {
            _messageCount++;
            return new AlertDecision(false, "Rate limit exceeded");
        }

        _messageCount++;
        return new AlertDecision(true, null);
    }

    private void ResetWindowIfNeeded()
    {
        var now = DateTime.UtcNow;
        if ((now - _windowStart).TotalMinutes >= 1)
        {
            _messageCount = 0;
            _windowStart = now;
        }
    }
}
