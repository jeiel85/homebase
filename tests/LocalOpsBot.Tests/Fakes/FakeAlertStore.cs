using LocalOpsBot.Core.Alerts;

namespace LocalOpsBot.Tests.Fakes;

public sealed class FakeAlertStore : IAlertStore
{
    private readonly List<AlertLogItem> _items = new();

    public Task InsertAsync(AlertLogItem entry, CancellationToken ct)
    {
        _items.Add(entry);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsRecentDedupKeyAsync(string dedupKey, TimeSpan window, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var exists = _items.Any(i => i.DedupKey == dedupKey && i.CreatedAt >= cutoff);
        return Task.FromResult(exists);
    }

    public Task<IReadOnlyList<AlertLogItem>> GetRecentAsync(int count, CancellationToken ct)
    {
        var result = _items.OrderByDescending(i => i.CreatedAt).Take(count).ToList();
        return Task.FromResult<IReadOnlyList<AlertLogItem>>(result);
    }
}
