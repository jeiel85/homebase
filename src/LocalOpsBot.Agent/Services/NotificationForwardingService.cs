using System.Runtime.Versioning;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Protocol.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class NotificationForwardingService : IHostedService
{
    private readonly INotificationBridgeServer _bridgeServer;
    private readonly IOutboundRouter _outbound;
    private readonly ITextMasker _masker;
    private readonly ILogger<NotificationForwardingService> _logger;

    public NotificationForwardingService(
        INotificationBridgeServer bridgeServer,
        IOutboundRouter outbound,
        ITextMasker masker,
        ILogger<NotificationForwardingService> logger)
    {
        _bridgeServer = bridgeServer;
        _outbound = outbound;
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

    private void OnNotificationReceived(
        ToastNotificationEvent notification)
    {
        _ = ForwardAsync(notification);
    }

    private async Task ForwardAsync(
        ToastNotificationEvent notification)
    {
        try
        {
            if (notification.Sensitivity ==
                NotificationSensitivity.Blocked)
            {
                _logger.LogInformation(
                    "Dropped blocked notification from {App}",
                    notification.SourceApp);
                return;
            }

            var title = _masker.Mask(
                notification.Title ?? string.Empty);
            var body = _masker.Mask(
                notification.Body ?? string.Empty);

            var outbound = new OutboundNotification(
                Guid.TryParse(notification.EventId, out var id)
                    ? id
                    : Guid.NewGuid(),
                "windows-toast",
                OutboundPriority.Info,
                string.IsNullOrWhiteSpace(title)
                    ? notification.SourceApp
                    : $"{notification.SourceApp}: {title}",
                body,
                $"toast:{notification.EventId}",
                notification.Sensitivity ==
                    NotificationSensitivity.Sensitive
                    ? MessageSensitivity.Sensitive
                    : MessageSensitivity.Normal,
                DeliveryPolicy.LocalPreferred,
                notification.CreatedAt,
                Metadata: new Dictionary<string, string>
                {
                    ["sourceApp"] = notification.SourceApp,
                    ["origin"] = "windows-toast",
                    ["rawNotificationId"] =
                        notification.RawNotificationId
                });

            var result = await _outbound.DeliverAsync(
                outbound,
                CancellationToken.None);

            if (result.Delivered)
            {
                _logger.LogInformation(
                    "Forwarded notification from {App}",
                    notification.SourceApp);
            }
            else
            {
                _logger.LogWarning(
                    "No channel delivered notification from {App}: {Error}",
                    notification.SourceApp,
                    result.CombinedError);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to forward notification");
        }
    }
}
