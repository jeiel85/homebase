namespace LocalOpsBot.Core.Monitoring;

public interface IProcessCollector
{
    Task<IReadOnlyList<ProcessWatchStatus>> CollectAsync(
        IReadOnlyList<ProcessWatchConfig> watches, CancellationToken ct);
}
