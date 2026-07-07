namespace LocalOpsBot.Core.Alerts;

public sealed record AlertDecision(bool Send, string? DropReason);

public interface IAlertPolicy
{
    Task<AlertDecision> ShouldSendAsync(AlertEvent alert, CancellationToken ct);
}
