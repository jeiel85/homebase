namespace LocalOpsBot.Core.Delivery;

public sealed class OutboundRouter : IOutboundRouter
{
    private readonly IReadOnlyDictionary<string, IOutboundChannel> _channels;

    public OutboundRouter(IEnumerable<IOutboundChannel> channels)
    {
        _channels = channels.ToDictionary(
            x => x.ChannelId,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<OutboundDeliveryResult> DeliverAsync(
        OutboundNotification notification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification.ExpiresAt is not null &&
            notification.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new OutboundDeliveryResult(
                false,
                [new OutboundAttemptResult(
                    "router",
                    false,
                    OutboundFailureKind.Expired,
                    "Notification has expired.")]);
        }

        var attempts = new List<OutboundAttemptResult>();

        // Each group is a fallback chain: try its channels in order and stop at the first success.
        // Groups are independent — Both delivers to the local group and to the remote chain.
        foreach (var group in SelectChannels(notification.Policy))
        {
            foreach (var channel in group)
            {
                var attempt = await TryAsync(channel, notification, ct);
                attempts.Add(attempt);
                if (attempt.Success) break;
            }
        }

        return new OutboundDeliveryResult(
            attempts.Any(x => x.Success),
            attempts);
    }

    private static async Task<OutboundAttemptResult> TryAsync(
        IOutboundChannel channel,
        OutboundNotification notification,
        CancellationToken ct)
    {
        if (!channel.IsAvailable)
        {
            return new OutboundAttemptResult(
                channel.ChannelId,
                false,
                OutboundFailureKind.NotConfigured,
                "Channel is not available.");
        }

        try
        {
            return await channel.SendAsync(notification, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OutboundAttemptResult(
                channel.ChannelId,
                false,
                OutboundFailureKind.Unknown,
                ex.Message);
        }
    }

    /// <summary>
    /// Channel order per policy. The phone push (<see cref="ChannelIds.UnifiedPush"/>) is the
    /// primary remote channel; Telegram is only the fallback when push is absent or fails
    /// (DECISIONS M-19, transition period). The Device Hub local slot (<see cref="ChannelIds.KdeConnect"/>)
    /// keeps its place in front.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<IOutboundChannel>> SelectChannels(DeliveryPolicy policy)
    {
        _channels.TryGetValue(ChannelIds.KdeConnect, out var local);
        _channels.TryGetValue(ChannelIds.UnifiedPush, out var push);
        _channels.TryGetValue(ChannelIds.Telegram, out var telegram);

        return policy switch
        {
            DeliveryPolicy.LocalOnly => Groups(Present(local)),
            DeliveryPolicy.TelegramOnly => Groups(Present(telegram)),
            DeliveryPolicy.Both => Groups(Present(local), Present(push, telegram)),
            DeliveryPolicy.LocalPreferred => Groups(Present(local, push, telegram)),
            DeliveryPolicy.TelegramFallback => Groups(Present(local, push, telegram)),
            DeliveryPolicy.EncryptedOnly => Groups(Present(local, push)),
            // A reply goes back where the request came from; Telegram is the only inbound channel.
            DeliveryPolicy.OriginOnly => Groups(Present(telegram)),
            _ => Array.Empty<IReadOnlyList<IOutboundChannel>>()
        };
    }

    private static IReadOnlyList<IReadOnlyList<IOutboundChannel>> Groups(
        params IReadOnlyList<IOutboundChannel>[] groups)
        => groups.Where(g => g.Count > 0).ToArray();

    private static IReadOnlyList<IOutboundChannel> Present(
        params IOutboundChannel?[] channels)
        => channels
            .Where(x => x is not null)
            .Cast<IOutboundChannel>()
            .ToArray();
}
