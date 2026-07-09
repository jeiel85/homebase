using LocalOpsBot.Core.Advisor;

namespace LocalOpsBot.Tests.Fakes;

internal sealed class FakePcStateAdvisor : IPcStateAdvisor
{
    public AdvisoryResult NextResult { get; set; } = new(true, "Consider closing heavy apps.", null);
    public int AdviseCallCount { get; private set; }

    public Task<AdvisoryResult> AdviseAsync(CancellationToken ct)
    {
        AdviseCallCount++;
        return Task.FromResult(NextResult);
    }

    public Task<string> BuildStateSummaryAsync(CancellationToken ct)
        => Task.FromResult("summary");
}
