namespace LocalOpsBot.Core.Monitoring;

public interface IWindowsServiceCollector
{
    Task<IReadOnlyList<WindowsServiceWatchStatus>> CollectAsync(
        IReadOnlyList<ServiceWatchConfig> watches, CancellationToken ct);
}
