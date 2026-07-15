using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Protocol.Messaging;
using Xunit;

namespace LocalOpsBot.Tests.Core.Delivery;

public sealed class OutboundRouterTests
{
    [Fact]
    public async Task TelegramFallback_UsesTelegramWhenLocalFails()
    {
        var local = new FakeChannel(
            "kdeconnect",
            new OutboundAttemptResult(
                "kdeconnect",
                false,
                OutboundFailureKind.EndpointOffline));

        var telegram = new FakeChannel(
            "telegram",
            new OutboundAttemptResult("telegram", true));

        var router = new OutboundRouter([local, telegram]);

        var result = await router.DeliverAsync(
            CreateNotification(DeliveryPolicy.TelegramFallback),
            CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.Equal(2, result.Attempts.Count);
    }

    [Fact]
    public async Task LocalPreferred_StopsAfterLocalSuccess()
    {
        var local = new FakeChannel(
            "kdeconnect",
            new OutboundAttemptResult("kdeconnect", true));

        var telegram = new FakeChannel(
            "telegram",
            new OutboundAttemptResult("telegram", true));

        var router = new OutboundRouter([local, telegram]);

        var result = await router.DeliverAsync(
            CreateNotification(DeliveryPolicy.LocalPreferred),
            CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.Single(result.Attempts);
        Assert.Equal(0, telegram.SendCount);
    }

    private static OutboundNotification CreateNotification(
        DeliveryPolicy policy)
        => new(
            Guid.NewGuid(),
            "test",
            OutboundPriority.Info,
            "Title",
            "Body",
            "test-key",
            MessageSensitivity.Normal,
            policy,
            DateTimeOffset.UtcNow);

    private sealed class FakeChannel : IOutboundChannel
    {
        private readonly OutboundAttemptResult _result;

        public FakeChannel(
            string channelId,
            OutboundAttemptResult result)
        {
            ChannelId = channelId;
            _result = result;
        }

        public string ChannelId { get; }

        public bool IsAvailable => true;

        public int SendCount { get; private set; }

        public Task<OutboundAttemptResult> SendAsync(
            OutboundNotification notification,
            CancellationToken ct)
        {
            SendCount++;
            return Task.FromResult(_result);
        }
    }
}
