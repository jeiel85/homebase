using System.Linq;

namespace LocalOpsBot.Core.Monitoring;

/// <summary>
/// Decides whether a Windows event should raise a Telegram alert, keeping event noise down: only
/// the configured <see cref="EventLogOptions.AlertLevels"/> push, and a repeating error (same
/// provider + event id) alerts at most once per <see cref="EventLogOptions.RepeatSuppressMinutes"/>.
/// Critical events always alert (never suppressed). In-memory, single-caller (the poller);
/// suppression state resets on Agent restart, which is acceptable.
/// </summary>
public sealed class EventAlertPolicy
{
    private readonly HashSet<string> _alertLevels;
    private readonly int _suppressMinutes;
    private readonly Dictionary<string, DateTimeOffset> _lastAlerted = new();

    public EventAlertPolicy(EventLogOptions options)
    {
        _alertLevels = new HashSet<string>(options.AlertLevels, StringComparer.OrdinalIgnoreCase);
        _suppressMinutes = options.RepeatSuppressMinutes;
    }

    /// <summary>
    /// Returns true if <paramref name="e"/> should alert now; updates repeat-suppression state as a
    /// side effect, so call once per event in arrival order.
    /// </summary>
    public bool ShouldAlert(WindowsEventLogItem e, DateTimeOffset now)
    {
        // Only configured levels push a Telegram alert at all.
        if (!_alertLevels.Contains(e.Level)) return false;

        // Critical is always delivered; only lower levels get repeat-suppressed.
        var isCritical = string.Equals(e.Level, "Critical", StringComparison.OrdinalIgnoreCase);
        if (isCritical || _suppressMinutes <= 0) return true;

        var key = $"{e.ProviderName}#{e.EventId}";
        if (_lastAlerted.TryGetValue(key, out var last) &&
            now - last < TimeSpan.FromMinutes(_suppressMinutes))
            return false; // same error within the window — stay quiet

        _lastAlerted[key] = now;
        Prune(now);
        return true;
    }

    // Keep the suppression map bounded: once it grows large, drop entries past the window
    // (they can no longer suppress anything).
    private void Prune(DateTimeOffset now)
    {
        if (_lastAlerted.Count < 256) return;
        var window = TimeSpan.FromMinutes(_suppressMinutes);
        foreach (var key in _lastAlerted.Where(kv => now - kv.Value >= window).Select(kv => kv.Key).ToList())
            _lastAlerted.Remove(key);
    }
}
