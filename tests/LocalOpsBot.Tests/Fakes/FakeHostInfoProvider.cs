using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Tests.Fakes;

internal sealed class FakeHostInfoProvider : IHostInfoProvider
{
    public HostInfoRecord NextResult { get; set; } = new(
        "TEST-PC", "192.168.1.100",
        DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
        TimeSpan.FromHours(2), "Microsoft Windows NT 10.0.22631.0");

    public Task<HostInfoRecord> GetHostInfoAsync(CancellationToken ct)
        => Task.FromResult(NextResult);
}
