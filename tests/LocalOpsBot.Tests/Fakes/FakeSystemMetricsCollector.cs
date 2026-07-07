using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Tests.Fakes;

internal sealed class FakeSystemMetricsCollector : ISystemMetricsCollector
{
    public string Name => "FakeMetrics";
    public CollectorResult<SystemMetricSnapshot> NextResult { get; set; }
        = CollectorResult<SystemMetricSnapshot>.Ok(
            new SystemMetricSnapshot(
                DateTimeOffset.UtcNow, 14.0, 32L * 1024 * 1024 * 1024,
                20L * 1024 * 1024 * 1024, 35.0,
                TimeSpan.FromDays(3), "TEST-PC", "Windows 11"),
            DateTimeOffset.UtcNow);

    public Task<CollectorResult<SystemMetricSnapshot>> CollectAsync(CancellationToken ct)
        => Task.FromResult(NextResult);
}
