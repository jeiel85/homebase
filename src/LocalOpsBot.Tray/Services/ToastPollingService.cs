using System.Runtime.Versioning;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Infrastructure.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.UI.Notifications.Management;

namespace LocalOpsBot.Tray.Services;

[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ToastPollingService : BackgroundService
{
    // Polls run every 3 s; an "alive" line every 20 polls was ~2,000 mostly empty lines a day (issue #4).
    private static readonly TimeSpan AliveLogInterval = TimeSpan.FromHours(1);

    private readonly WindowsToastNotificationListener _listener;
    private readonly INotificationFilter _filter;
    private readonly ITextMasker _masker;
    private readonly INotificationBridgeClient _bridge;
    private readonly ILogger<ToastPollingService> _logger;

    public ToastPollingService(
        WindowsToastNotificationListener listener,
        INotificationFilter filter,
        ITextMasker masker,
        INotificationBridgeClient bridge,
        ILogger<ToastPollingService> logger)
    {
        _listener = listener;
        _filter = filter;
        _masker = masker;
        _bridge = bridge;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Toast polling service starting");

        UserNotificationListenerAccessStatus accessStatus;
        try
        {
            accessStatus = await _listener.RequestAccessAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Requesting toast notification access threw");
            return;
        }
        if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
        {
            _logger.LogWarning("Toast notification access denied: {Status}", accessStatus);
            return;
        }

        _logger.LogInformation("Toast notification access granted");

        // Baseline: seed the listener's seen-ids with the current Action Center backlog so only
        // notifications that arrive AFTER forwarding starts get sent — don't dump the backlog.
        try
        {
            var backlog = await _listener.PollAsync(ct);
            foreach (var b in backlog) ForwardingApps.RecordSeenApp(b.SourceApp);
            _logger.LogInformation("Baselined {Count} existing notification(s); not forwarded", backlog.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Baseline poll failed");
        }

        var pollCount = 0;
        var lastLogged = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                var notifications = await _listener.PollAsync(ct);

                pollCount++;
                if (notifications.Count > 0)
                {
                    _logger.LogInformation("Poll #{N} returned {Count} new notification(s)", pollCount, notifications.Count);
                    lastLogged = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastLogged >= AliveLogInterval)
                {
                    // A quiet hour still leaves one line, so the log shows the poller was alive.
                    _logger.LogInformation("Poll #{N}: alive, no new notifications in the last hour", pollCount);
                    lastLogged = DateTime.UtcNow;
                }

                foreach (var notification in notifications)
                {
                    // Record every app that sends a notification so the dashboard can offer it,
                    // even ones the filter will drop.
                    ForwardingApps.RecordSeenApp(notification.SourceApp);

                    var filterResult = _filter.Evaluate(notification);
                    if (!filterResult.Allowed)
                    {
                        // Blocked notifications are dropped here so their content never
                        // even crosses the pipe to the Agent.
                        _logger.LogInformation("Notification from '{App}' blocked: {Reason}",
                            notification.SourceApp, filterResult.DropReason);
                        continue;
                    }

                    var masked = notification with
                    {
                        Title = _masker.Mask(notification.Title ?? string.Empty),
                        Body = _masker.Mask(notification.Body ?? string.Empty),
                        Sensitivity = NotificationSensitivity.Normal
                    };

                    await _bridge.SendAsync(masked, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Toast polling failed");
            }
        }
    }
}
