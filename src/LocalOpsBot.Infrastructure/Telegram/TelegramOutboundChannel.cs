using LocalOpsBot.Core.Delivery;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Infrastructure.Telegram;

public sealed class TelegramOutboundChannel : IOutboundChannel
{
    private readonly ITelegramClient _telegram;
    private readonly IOptions<TelegramOptions> _options;
    private readonly TelegramNotificationRenderer _renderer;

    public TelegramOutboundChannel(
        ITelegramClient telegram,
        IOptions<TelegramOptions> options,
        TelegramNotificationRenderer renderer)
    {
        _telegram = telegram;
        _options = options;
        _renderer = renderer;
    }

    public string ChannelId => "telegram";

    public bool IsAvailable =>
        _telegram.IsConfigured &&
        _options.Value.AllowedChatIds.Any(x => x != 0);

    public async Task<OutboundAttemptResult> SendAsync(
        OutboundNotification notification,
        CancellationToken ct)
    {
        var chatId = _options.Value.AllowedChatIds.FirstOrDefault(x => x != 0);
        if (chatId == 0)
        {
            return new OutboundAttemptResult(
                ChannelId,
                false,
                OutboundFailureKind.NotConfigured,
                "No allowed Telegram chat is configured.");
        }

        try
        {
            await _telegram.SendMessageAsync(
                chatId,
                _renderer.Render(notification),
                null,
                ct);

            return new OutboundAttemptResult(ChannelId, true);
        }
        catch (TelegramApiException ex) when (ex.HttpStatusCode == 401)
        {
            return new OutboundAttemptResult(
                ChannelId,
                false,
                OutboundFailureKind.Unauthorized,
                ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new OutboundAttemptResult(
                ChannelId,
                false,
                OutboundFailureKind.Transient,
                ex.Message);
        }
        catch (Exception ex)
        {
            return new OutboundAttemptResult(
                ChannelId,
                false,
                OutboundFailureKind.Unknown,
                ex.Message);
        }
    }
}
