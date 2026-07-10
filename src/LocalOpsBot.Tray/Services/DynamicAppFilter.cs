using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// App-name filter whose block-list is read live from the user file (<see cref="ForwardingApps"/>),
/// so the dashboard's app exclusions take effect within a poll cycle (~3s) without restarting the
/// tray or elevating. Everything forwards <b>except</b> the listed apps — so a newly-seen app is
/// never silently dropped; the user only excludes the noisy ones (e.g. a phone-mirroring app that
/// would otherwise create a forwarding loop).
/// </summary>
internal sealed class DynamicAppFilter : INotificationFilter
{
    public NotificationFilterResult Evaluate(ToastNotificationEvent notification)
    {
        var block = ForwardingApps.ReadBlockList();
        if (block.Count > 0 && block.Contains(notification.SourceApp, StringComparer.OrdinalIgnoreCase))
            return new NotificationFilterResult(false, "App excluded");

        return new NotificationFilterResult(true, null);
    }
}
