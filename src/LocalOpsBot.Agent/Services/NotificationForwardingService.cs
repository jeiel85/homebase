using System.Runtime.Versioning;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class NotificationForwardingService : IHostedService
{
    private readonly INotificationBridgeServer _bridgeServer;
    private readonly ITelegramClient _telegram;
    private readonly IOptions<TelegramOptions> _options;
    private readonly ITextMasker _masker;
    private readonly ILogger<NotificationForwardingService> _logger;

    public NotificationForwardingService(
        INotificationBridgeServer bridgeServer,
        ITelegramClient telegram,
        IOptions<TelegramOptions> options,
        ITextMasker masker,
        ILogger<NotificationForwardingService> logger)
    {
        _bridgeServer = bridgeServer;
        _telegram = telegram;
        _options = options;
        _masker = masker;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _bridgeServer.NotificationReceived += OnNotificationReceived;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _bridgeServer.NotificationReceived -= OnNotificationReceived;
        return Task.CompletedTask;
    }

    private async void OnNotificationReceived(ToastNotificationEvent notification)
    {
        try
        {
            // Defense in depth: even if a blocked notification reaches the Agent,
            // never forward it to Telegram.
            if (notification.Sensitivity == NotificationSensitivity.Blocked)
            {
                _logger.LogInformation("Dropped blocked notification from {App}", notification.SourceApp);
                return;
            }

            var targetChatId = _options.Value.AllowedChatIds.FirstOrDefault();
            if (targetChatId == 0) return;

            var title = _masker.Mask(notification.Title ?? string.Empty);
            var body = _masker.Mask(notification.Body ?? string.Empty);

            var text = $"<b>\U0001f514 {HtmlEscape(notification.SourceApp)}</b>\n" +
                       $"Title: {HtmlEscape(title)}\n" +
                       $"Body: {HtmlEscape(body)}";

            await _telegram.SendMessageAsync(targetChatId, text, null, CancellationToken.None);
            _logger.LogInformation("Forwarded notification from {App}", notification.SourceApp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward notification");
        }
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
