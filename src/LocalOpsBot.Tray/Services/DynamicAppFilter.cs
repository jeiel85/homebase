using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// App-name filter whose block-list is read live from the user file (<see cref="ForwardingApps"/>),
/// so the dashboard's app exclusions take effect within a poll cycle (~3s) without restarting the
/// tray or elevating. Everything forwards <b>except</b> the listed apps — so a newly-seen app is
/// never silently dropped; the user only excludes the noisy ones (e.g. a phone-mirroring app that
/// would otherwise create a forwarding loop).
/// The config filter (<c>notificationForwarding:blockApps</c> — password managers and
/// authenticators by default — and AllowList mode) is applied first and cannot be overridden here.
/// </summary>
internal sealed class DynamicAppFilter : INotificationFilter
{
    private readonly INotificationFilter? _config;

    public DynamicAppFilter(INotificationFilter? config = null) => _config = config;

    public NotificationFilterResult Evaluate(ToastNotificationEvent notification)
    {
        // A notification this Tray showed for another device (M-28) must not be forwarded back.
        if (Mobile.OwnToasts.IsOwn(notification)) return new NotificationFilterResult(false, "Relayed by Homebase");
        if (_config?.Evaluate(notification) is { Allowed: false } denied) return denied;

        var block = ForwardingApps.ReadBlockList();
        if (block.Count > 0 && block.Contains(notification.SourceApp, StringComparer.OrdinalIgnoreCase))
            return new NotificationFilterResult(false, "App excluded");

        return new NotificationFilterResult(true, null);
    }
}
