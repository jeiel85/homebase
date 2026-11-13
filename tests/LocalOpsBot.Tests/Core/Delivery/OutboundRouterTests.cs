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

    private static FakeChannel Ok(string id) => new(id, new OutboundAttemptResult(id, true));

    private static FakeChannel Fail(string id, OutboundFailureKind kind = OutboundFailureKind.Transient) =>
        new(id, new OutboundAttemptResult(id, false, kind));

    [Theory]
    [InlineData(DeliveryPolicy.TelegramFallback)]
    [InlineData(DeliveryPolicy.LocalPreferred)]
    [InlineData(DeliveryPolicy.Both)]
    public async Task Push_is_preferred_and_telegram_is_not_used_when_push_succeeds(DeliveryPolicy policy)
    {
        var push = Ok("unifiedpush");
        var telegram = Ok("telegram");

        var result = await new OutboundRouter([telegram, push]).DeliverAsync(CreateNotification(policy), default);

        Assert.True(result.Delivered);
        Assert.Equal(1, push.SendCount);
        Assert.Equal(0, telegram.SendCount);
    }

    [Theory]
    [InlineData(OutboundFailureKind.RateLimited)]
    [InlineData(OutboundFailureKind.Transient)]
    [InlineData(OutboundFailureKind.Expired)]
    public async Task Telegram_takes_over_when_push_fails(OutboundFailureKind kind)
    {
        var push = Fail("unifiedpush", kind);
        var telegram = Ok("telegram");

        var result = await new OutboundRouter([push, telegram]).DeliverAsync(
            CreateNotification(DeliveryPolicy.TelegramFallback), default);

        Assert.True(result.Delivered);
        Assert.Equal(new[] { "unifiedpush", "telegram" }, result.Attempts.Select(a => a.ChannelId));
    }

    [Fact]
    public async Task Both_still_delivers_to_the_local_channel_and_the_remote_chain()
    {
        var local = Ok("kdeconnect");
        var push = Fail("unifiedpush");
        var telegram = Ok("telegram");

        var result = await new OutboundRouter([local, push, telegram]).DeliverAsync(
            CreateNotification(DeliveryPolicy.Both), default);

        Assert.Equal(new[] { "kdeconnect", "unifiedpush", "telegram" }, result.Attempts.Select(a => a.ChannelId));
        Assert.Equal(1, local.SendCount);
        Assert.Equal(1, telegram.SendCount);
    }

    [Fact]
    public async Task Local_success_still_short_circuits_push_and_telegram()
    {
        var local = Ok("kdeconnect");
        var push = Ok("unifiedpush");
        var telegram = Ok("telegram");

        await new OutboundRouter([local, push, telegram]).DeliverAsync(
            CreateNotification(DeliveryPolicy.TelegramFallback), default);

        Assert.Equal(0, push.SendCount);
        Assert.Equal(0, telegram.SendCount);
    }

    [Fact]
    public async Task EncryptedOnly_never_falls_back_to_telegram()
    {
        var push = Fail("unifiedpush");
        var telegram = Ok("telegram");

        var result = await new OutboundRouter([push, telegram]).DeliverAsync(
            CreateNotification(DeliveryPolicy.EncryptedOnly), default);

        Assert.False(result.Delivered);
        Assert.Equal(0, telegram.SendCount);
    }

    [Fact]
    public async Task TelegramOnly_and_OriginOnly_skip_push()
    {
        var push = Ok("unifiedpush");
        var telegram = Ok("telegram");
        var router = new OutboundRouter([push, telegram]);

        await router.DeliverAsync(CreateNotification(DeliveryPolicy.TelegramOnly), default);
        await router.DeliverAsync(CreateNotification(DeliveryPolicy.OriginOnly), default);

        Assert.Equal(0, push.SendCount);
        Assert.Equal(2, telegram.SendCount);
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
