namespace LocalOpsBot.Core.Monitoring;

public interface IEventLogWatcher
{
    /// <summary>
    /// Stateful poll for the alert pipeline: returns only events written since the previous
    /// poll (resuming from a per-log bookmark) and advances that bookmark. Call from a single
    /// caller (the background poller) — it mutates shared resume state.
    /// </summary>
    Task<IReadOnlyList<WindowsEventLogItem>> PollAsync(EventLogOptions options, CancellationToken ct);

    /// <summary>
    /// Stateless on-demand read for <c>/events</c>: the newest <paramref name="limit"/> matching
    /// events, newest first. Does not touch the poll bookmark, so it never hides events from the
    /// alert pipeline nor races the poller.
    /// </summary>
    Task<IReadOnlyList<WindowsEventLogItem>> ReadRecentAsync(EventLogOptions options, int limit, CancellationToken ct);
}
