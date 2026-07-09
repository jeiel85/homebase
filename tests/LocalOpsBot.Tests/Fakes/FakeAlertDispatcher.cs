using LocalOpsBot.Core.Alerts;

namespace LocalOpsBot.Tests.Fakes;

internal sealed class FakeAlertDispatcher : IAlertDispatcher
{
    public List<AlertEvent> Dispatched { get; } = new();

    public Task DispatchAsync(AlertEvent alert, CancellationToken ct)
    {
        Dispatched.Add(alert);
        return Task.CompletedTask;
    }
}
