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

/// <summary>
/// One channel's try. <c>Note</c> says, for a success, what it actually did when that is worth telling apart in
/// the log — e.g. which phones a push went to, or that every phone was skipped and nothing was sent.
/// </summary>
public sealed record OutboundAttemptResult(
    string ChannelId,
    bool Success,
    OutboundFailureKind FailureKind = OutboundFailureKind.None,
    string? Error = null,
    string? Note = null);

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

    /// <summary>What the channels that succeeded did ("unifiedpush: pushed to 2 phone(s): …"), or null when none said.</summary>
    public string? SuccessNotes =>
        Attempts.Where(x => x.Success && x.Note is not null).Select(x => $"{x.ChannelId}: {x.Note}").ToList() is { Count: > 0 } notes
            ? string.Join(" | ", notes)
            : null;
}
