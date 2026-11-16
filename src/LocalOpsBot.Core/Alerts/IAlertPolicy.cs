namespace LocalOpsBot.Core.Alerts;

public sealed record AlertDecision(bool Send, string? DropReason);

public interface IAlertPolicy
{
    Task<AlertDecision> ShouldSendAsync(AlertEvent alert, CancellationToken ct);

    /// <summary>
    /// Whether an alert that already passed <see cref="ShouldSendAsync"/> and failed to deliver may still
    /// be re-sent now (#2): only what the user changed since counts (a mute). Dedup and the rate limit
    /// are not applied again — the alert's own failed row would count as its duplicate.
    /// </summary>
    Task<AlertDecision> ShouldResendAsync(AlertEvent alert, CancellationToken ct) =>
        Task.FromResult(new AlertDecision(true, null));
}
