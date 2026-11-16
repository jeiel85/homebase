using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Infrastructure.Telegram;
using LocalOpsBot.Protocol.Messaging;
using LocalOpsBot.Tests.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure.Telegram;

/// <summary>Failure kinds decide whether an alert is re-sent (#2): only what can pass is retried.</summary>
public sealed class TelegramOutboundChannelTests
{
    private static OutboundNotification Notification() => new(
        Guid.NewGuid(), "event_log", OutboundPriority.Warning, "Title", "Body", "k",
        MessageSensitivity.Normal, DeliveryPolicy.TelegramOnly, DateTimeOffset.UtcNow);

    private static Task<OutboundAttemptResult> SendThrough(Exception error) =>
        new TelegramOutboundChannel(
                new ThrowingClient(error),
                Options.Create(new TelegramOptions { AllowedChatIds = new long[] { 42 } }),
                new TelegramNotificationRenderer())
            .SendAsync(Notification(), CancellationToken.None);

    [Theory]
    [InlineData(401, OutboundFailureKind.Unauthorized)]
    [InlineData(429, OutboundFailureKind.RateLimited)]
    [InlineData(502, OutboundFailureKind.Transient)]
    [InlineData(400, OutboundFailureKind.Permanent)]
    [InlineData(403, OutboundFailureKind.Permanent)]
    public async Task Api_errors_are_classified_by_status(int status, OutboundFailureKind expected)
    {
        var result = await SendThrough(new TelegramApiException(status, $"Telegram sendMessage failed ({status})"));

        Assert.False(result.Success);
        Assert.Equal(ChannelIds.Telegram, result.ChannelId);
        Assert.Equal(expected, result.FailureKind);
    }

    [Fact]
    public async Task Network_errors_and_timeouts_are_transient()
    {
        Assert.Equal(OutboundFailureKind.Transient,
            (await SendThrough(new HttpRequestException("No such host is known."))).FailureKind);
        Assert.Equal(OutboundFailureKind.Transient,
            (await SendThrough(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"))).FailureKind);
    }

    private sealed class ThrowingClient(Exception error) : FakeTelegramClient
    {
        public override Task SendMessageAsync(long chatId, string text, TelegramSendOptions? options, CancellationToken ct) =>
            Task.FromException(error);
    }
}
