using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Hands relayed notifications (DECISIONS M-28) to the Tray over the mobile bridge. When no Tray is
/// connected (nobody logged on) the notification is still pushed to phones; it is just not shown here.
/// </summary>
public sealed class TrayToastRelay : ITrayToaster
{
    public event Action<TrayToast>? Requested;

    public void Show(string title, string? body) => Requested?.Invoke(new TrayToast(title, body));
}
