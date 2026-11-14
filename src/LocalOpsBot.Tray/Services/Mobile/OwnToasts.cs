using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Notifications the Tray itself showed for another device (DECISIONS M-28). The Windows notification
/// listener sees them like any other toast; forwarding them again would echo every relayed
/// notification back to the phones. Matched by title within two minutes, and by the app name as a
/// second guard.
/// </summary>
internal static class OwnToasts
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    private static readonly List<(string Title, DateTime At)> Shown = new();

    public static void Remember(string title)
    {
        lock (Shown)
        {
            Shown.RemoveAll(s => DateTime.UtcNow - s.At > Window);
            Shown.Add((title.Trim(), DateTime.UtcNow));
        }
    }

    public static bool IsOwn(ToastNotificationEvent n)
    {
        if (n.SourceApp.StartsWith("Homebase", StringComparison.OrdinalIgnoreCase)) return true;
        var title = (n.Title ?? "").Trim();
        if (title.Length == 0) return false;
        lock (Shown) return Shown.Any(s => DateTime.UtcNow - s.At <= Window && s.Title == title);
    }
}
