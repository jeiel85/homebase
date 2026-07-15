namespace LocalOpsBot.Core.Delivery;

public enum OutboundFailureKind
{
    None,
    NotConfigured,
    EndpointOffline,
    Unauthorized,
    RateLimited,
    Transient,
    Permanent,
    Expired,
    Unknown
}

public sealed record OutboundAttemptResult(
    string ChannelId,
    bool Success,
    OutboundFailureKind FailureKind = OutboundFailureKind.None,
    string? Error = null);

public sealed record OutboundDeliveryResult(
    bool Delivered,
    IReadOnlyList<OutboundAttemptResult> Attempts)
{
    public string? CombinedError =>
        Attempts.Count == 0
            ? "No outbound channel was selected."
            : string.Join(
                " | ",
                Attempts
                    .Where(x => !x.Success)
                    .Select(x => $"{x.ChannelId}: {x.Error ?? x.FailureKind.ToString()}"));
}
