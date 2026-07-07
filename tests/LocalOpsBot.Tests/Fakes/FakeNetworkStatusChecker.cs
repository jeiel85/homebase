using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Tests.Fakes;

internal sealed class FakeNetworkStatusChecker : INetworkStatusChecker
{
    public string Name => "FakeNetwork";
    public CollectorResult<NetworkStatusSnapshot> NextResult { get; set; }
        = CollectorResult<NetworkStatusSnapshot>.Ok(
            new NetworkStatusSnapshot(true, "192.168.1.100", null,
                new[] { "Ethernet0" }, 5, null),
            DateTimeOffset.UtcNow);

    public Task<CollectorResult<NetworkStatusSnapshot>> CollectAsync(CancellationToken ct)
        => Task.FromResult(NextResult);
}
