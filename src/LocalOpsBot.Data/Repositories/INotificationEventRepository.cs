using LocalOpsBot.Data.Models;

namespace LocalOpsBot.Data.Repositories;

public interface INotificationEventRepository
{
    Task InsertAsync(NotificationEventEntry entry, CancellationToken ct);
    Task<IReadOnlyList<NotificationEventEntry>> GetRecentAsync(int count, CancellationToken ct);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
}
