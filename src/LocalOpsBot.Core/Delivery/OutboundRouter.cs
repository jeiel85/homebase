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

        var selected = SelectChannels(notification.Policy);
        var attempts = new List<OutboundAttemptResult>();

        foreach (var channel in selected)
        {
            if (!channel.IsAvailable)
            {
                attempts.Add(new OutboundAttemptResult(
                    channel.ChannelId,
                    false,
                    OutboundFailureKind.NotConfigured,
                    "Channel is not available."));
                continue;
            }

            try
            {
                var attempt = await channel.SendAsync(notification, ct);
                attempts.Add(attempt);

                if ((notification.Policy is DeliveryPolicy.LocalPreferred or
                     DeliveryPolicy.TelegramFallback) &&
                    attempt.Success)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempts.Add(new OutboundAttemptResult(
                    channel.ChannelId,
                    false,
                    OutboundFailureKind.Unknown,
                    ex.Message));
            }
        }

        return new OutboundDeliveryResult(
            attempts.Any(x => x.Success),
            attempts);
    }

    private IReadOnlyList<IOutboundChannel> SelectChannels(DeliveryPolicy policy)
    {
        _channels.TryGetValue("kdeconnect", out var local);
        _channels.TryGetValue("telegram", out var telegram);

        return policy switch
        {
            DeliveryPolicy.LocalOnly => Present(local),
            DeliveryPolicy.TelegramOnly => Present(telegram),
            DeliveryPolicy.Both => Present(local, telegram),
            DeliveryPolicy.LocalPreferred => Present(local, telegram),
            DeliveryPolicy.TelegramFallback => Present(local, telegram),
            DeliveryPolicy.OriginOnly => Present(telegram),
            _ => Array.Empty<IOutboundChannel>()
        };
    }

    private static IReadOnlyList<IOutboundChannel> Present(
        params IOutboundChannel?[] channels)
        => channels
            .Where(x => x is not null)
            .Cast<IOutboundChannel>()
            .ToArray();
}
