using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Data.Repositories;

namespace LocalOpsBot.Data.Adapters;

public sealed class AlertStoreAdapter : IAlertStore
{
    private readonly IAlertLogRepository _inner;

    public AlertStoreAdapter(IAlertLogRepository inner) => _inner = inner;

    public Task InsertAsync(AlertLogItem entry, CancellationToken ct)
        => _inner.InsertAsync(new Models.AlertLogEntry(
            entry.Id, entry.AlertId, entry.Kind, entry.Severity, entry.Title,
            entry.Body, entry.DedupKey, entry.Source, entry.Status,
            entry.Error, entry.CreatedAt, entry.SentAt), ct);

    public Task<bool> ExistsRecentDedupKeyAsync(string dedupKey, TimeSpan window, CancellationToken ct)
        => _inner.ExistsRecentDedupKeyAsync(dedupKey, window, ct);

    public async Task<IReadOnlyList<AlertLogItem>> GetRecentAsync(int count, CancellationToken ct)
    {
        var entries = await _inner.GetRecentAsync(count, ct);
        return entries.Select(e => new AlertLogItem(
            e.Id, e.AlertId, e.Kind, e.Severity, e.Title,
            e.Body, e.DedupKey, e.Source, e.Status,
            e.Error, e.CreatedAt, e.SentAt)).ToList();
    }
}
