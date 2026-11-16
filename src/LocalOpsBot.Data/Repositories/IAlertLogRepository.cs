namespace LocalOpsBot.Data.Repositories;

public interface IAlertLogRepository
{
    Task InsertAsync(Models.AlertLogEntry entry, CancellationToken ct);
    Task<bool> ExistsRecentDedupKeyAsync(string dedupKey, TimeSpan window, CancellationToken ct);
    Task<IReadOnlyList<Models.AlertLogEntry>> GetRecentAsync(int count, CancellationToken ct);

    /// <summary>Marks a <c>Failed</c> row as <c>Sent</c> once a re-send delivered it; the first error is kept.</summary>
    Task MarkSentAsync(string alertId, DateTimeOffset sentAt, CancellationToken ct);
}
