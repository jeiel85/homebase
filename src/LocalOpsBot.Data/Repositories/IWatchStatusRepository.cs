using LocalOpsBot.Data.Models;

namespace LocalOpsBot.Data.Repositories;

public interface IWatchStatusRepository
{
    Task InsertAsync(WatchStatusEntry entry, CancellationToken ct);
    Task<WatchStatusEntry?> GetLatestAsync(string watchName, CancellationToken ct);
    Task<IReadOnlyList<WatchStatusEntry>> GetRecentAsync(int count, CancellationToken ct);
}
