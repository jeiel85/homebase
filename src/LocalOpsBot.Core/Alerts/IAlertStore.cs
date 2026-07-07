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
}
