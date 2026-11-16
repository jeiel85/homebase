namespace LocalOpsBot.Core.Alerts;

public sealed record AlertLogItem(
    long? Id, string AlertId, string Kind, string Severity,
    string Title, string? Body, string? DedupKey, string? Source,
    string Status, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

public interface IAlertStore
{
    Task InsertAsync(AlertLogItem entry, CancellationToken ct);
    Task<bool> ExistsRecentDedupKeyAsync(string dedupKey, TimeSpan window, CancellationToken ct);
    Task<IReadOnlyList<AlertLogItem>> GetRecentAsync(int count, CancellationToken ct);

    /// <summary>
    /// Records that an alert logged as <c>Failed</c> was delivered by a later re-send (#2). The default
    /// does nothing, for stores that keep no delivery state.
    /// </summary>
    Task MarkSentAsync(string alertId, DateTimeOffset sentAt, CancellationToken ct) => Task.CompletedTask;
}
