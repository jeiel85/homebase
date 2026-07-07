using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Tests.Fakes;

internal sealed class FakeDiskCollector : IDiskCollector
{
    public string Name => "FakeDisk";
    public CollectorResult<IReadOnlyList<DiskSnapshot>> NextResult { get; set; }
        = CollectorResult<IReadOnlyList<DiskSnapshot>>.Ok(
            new List<DiskSnapshot>
            {
                new("C:\\", "Fixed", 500L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024,
                    400L * 1024 * 1024 * 1024, 80.0, true)
            },
            DateTimeOffset.UtcNow);

    public Task<CollectorResult<IReadOnlyList<DiskSnapshot>>> CollectAsync(CancellationToken ct)
        => Task.FromResult(NextResult);
}
