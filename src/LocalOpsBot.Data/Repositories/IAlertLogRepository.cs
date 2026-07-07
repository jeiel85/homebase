namespace LocalOpsBot.Data.Repositories;

public interface IAlertLogRepository
{
    Task InsertAsync(Models.AlertLogEntry entry, CancellationToken ct);
    Task<bool> ExistsRecentDedupKeyAsync(string dedupKey, TimeSpan window, CancellationToken ct);
    Task<IReadOnlyList<Models.AlertLogEntry>> GetRecentAsync(int count, CancellationToken ct);
}
