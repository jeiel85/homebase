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

    public Task MarkSentAsync(string alertId, DateTimeOffset sentAt, CancellationToken ct)
    {
        var i = _items.FindIndex(x => x.AlertId == alertId && x.Status == "Failed");
        if (i >= 0) _items[i] = _items[i] with { Status = "Sent", SentAt = sentAt };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AlertLogItem>> GetRecentAsync(int count, CancellationToken ct)
    {
        var result = _items.OrderByDescending(i => i.CreatedAt).Take(count).ToList();
        return Task.FromResult<IReadOnlyList<AlertLogItem>>(result);
    }
}
