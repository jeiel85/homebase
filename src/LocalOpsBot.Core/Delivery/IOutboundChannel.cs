namespace LocalOpsBot.Core.Delivery;

public interface IOutboundChannel
{
    string ChannelId { get; }

    bool IsAvailable { get; }

    Task<OutboundAttemptResult> SendAsync(
        OutboundNotification notification,
        CancellationToken ct);
}
