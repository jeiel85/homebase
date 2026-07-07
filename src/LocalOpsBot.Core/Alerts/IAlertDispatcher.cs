namespace LocalOpsBot.Core.Alerts;

public interface IAlertDispatcher
{
    Task DispatchAsync(AlertEvent alert, CancellationToken ct);
}
