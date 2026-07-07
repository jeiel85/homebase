namespace LocalOpsBot.Core.Monitoring;

public interface ICollector<TSnapshot>
{
    string Name { get; }
    Task<CollectorResult<TSnapshot>> CollectAsync(CancellationToken ct);
}

public sealed record CollectorResult<T>(
    bool Success,
    T? Snapshot,
    string? Error,
    DateTimeOffset CollectedAt)
{
    public static CollectorResult<T> Ok(T snapshot, DateTimeOffset at) => new(true, snapshot, null, at);
    public static CollectorResult<T> Fail(string error, DateTimeOffset at) => new(false, default, error, at);
}
