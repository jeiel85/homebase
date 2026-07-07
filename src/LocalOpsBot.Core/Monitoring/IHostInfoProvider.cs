namespace LocalOpsBot.Core.Monitoring;

public interface IHostInfoProvider
{
    Task<HostInfoRecord> GetHostInfoAsync(CancellationToken ct);
}
