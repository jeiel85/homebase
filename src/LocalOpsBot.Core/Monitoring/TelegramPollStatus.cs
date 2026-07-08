namespace LocalOpsBot.Core.Monitoring;

/// <summary>
/// Shared, thread-safe view of the Telegram long-poll loop's health, written by the polling
/// background service and read by <c>/diagnostics</c>. A single singleton instance is shared
/// across both so the command can report whether polling is actually alive.
/// </summary>
public interface ITelegramPollStatus
{
    /// <summary>UTC time of the last successful getUpdates round-trip, or null if none yet.</summary>
    DateTimeOffset? LastSuccessfulPollUtc { get; }

    /// <summary>Consecutive poll failures since the last success (0 when healthy).</summary>
    int ConsecutiveFailures { get; }

    void RecordSuccess();
    void RecordFailure();
}

public sealed class TelegramPollStatus : ITelegramPollStatus
{
    private long _lastPollTicks;      // UTC ticks; 0 = never
    private int _consecutiveFailures;

    public DateTimeOffset? LastSuccessfulPollUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPollTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _lastPollTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    public void RecordFailure() => Interlocked.Increment(ref _consecutiveFailures);
}
