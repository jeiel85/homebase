namespace LocalOpsBot.Core.Delivery;

public interface IOutboundRouter
{
    Task<OutboundDeliveryResult> DeliverAsync(
        OutboundNotification notification,
        CancellationToken ct);
}
