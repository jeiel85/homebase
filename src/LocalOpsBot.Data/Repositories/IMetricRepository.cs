using LocalOpsBot.Data.Models;

namespace LocalOpsBot.Data.Repositories;

public interface IMetricRepository
{
    Task InsertAsync(MetricSampleEntry entry, CancellationToken ct);
    Task<IReadOnlyList<MetricSampleEntry>> GetRecentAsync(int count, CancellationToken ct);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
}
