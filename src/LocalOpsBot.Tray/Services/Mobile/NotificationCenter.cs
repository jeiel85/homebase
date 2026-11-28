using System.Globalization;
using System.IO;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Tray side of the notification center (DECISIONS M-32): reads the Agent's history over the mobile
/// bridge and keeps, per Windows user, when the center was last looked at. Opening the center marks
/// everything shown as read, like the phone app's center.
/// </summary>
internal static class NotificationCenter
{
    /// <summary>Unread entries counted at most this far (the label then says "99+").</summary>
    private const int CountWindow = NotificationHistoryRules.MaxPage;

    private static readonly object Gate = new();

    private static string SeenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "notification-center-seen.txt");

    /// <summary>The last unread count. Raised on the UI thread.</summary>
    public static int Unread { get; private set; }

    public static event Action<int>? UnreadChanged;

    /// <summary>
    /// Design preview only (<c>--preview-dashboard</c>): made-up history instead of the bridge, and a
    /// read marker kept in memory so the preview never touches this user's real one.
    /// </summary>
    internal static HistoryPage? Preview { get; set; }

    private static DateTimeOffset _previewSeen = DateTimeOffset.UtcNow.AddHours(-1);

    /// <summary>
    /// When this user last opened the center. The first time ever it is "now", so a new install does
    /// not greet the user with 30 days of old alerts as unread.
    /// </summary>
    public static DateTimeOffset LastSeen()
    {
        if (Preview is not null) return _previewSeen;
        lock (Gate)
        {
            if (Read() is { } seen) return seen;
            var now = DateTimeOffset.UtcNow;
            Write(now);
            return now;
        }
    }

    /// <summary>Marks everything up to <paramref name="at"/> as read (never moves the marker back).</summary>
    public static void MarkSeen(DateTimeOffset at)
    {
        if (Preview is not null)
        {
            if (at > _previewSeen) _previewSeen = at;
        }
        else
        {
            lock (Gate)
            {
                if (Read() is not { } seen || at > seen) Write(at);
            }
        }
        SetUnread(0);
    }

    private static DateTimeOffset? Read()
    {
        try
        {
            if (File.Exists(SeenPath) && DateTimeOffset.TryParse(File.ReadAllText(SeenPath).Trim(),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen))
                return seen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static void Write(DateTimeOffset at)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SeenPath)!);
            File.WriteAllText(SeenPath, at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the count may show read entries again after a restart, nothing worse.
        }
    }

    /// <summary>Outcome of a history request.</summary>
    public enum Failure { None, FeatureOff, NoAgent, Failed }

    /// <summary>One page of history, newest first, or why there is none.</summary>
    public static async Task<(HistoryPage? Page, Failure Failure)> ListAsync(HistoryCursor? after, int limit)
    {
        if (Preview is { } preview) return (after is null ? preview : new HistoryPage(Array.Empty<HistoryEntry>(), false), Failure.None);
        if (MobileHost.Client is not { } client) return (null, Failure.FeatureOff);
        if (!client.IsConnected) return (null, Failure.NoAgent);
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpHistoryList, new HistoryListRequest(after, limit));
            if ((bool?)r["ok"] != true) return (null, Failure.Failed);
            var page = r["payload"]?.Deserialize<HistoryPage>(MobileProtocol.Json);
            return page is null ? (null, Failure.Failed) : (page, Failure.None);
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            return (null, Failure.NoAgent);
        }
    }

    /// <summary>
    /// Deletes the relayed notifications — all of them, or only <paramref name="scope"/>'s (one app or one sender, M-81);
    /// the alert log stays. False when the Agent refused or was unreachable.
    /// </summary>
    public static async Task<bool> ClearAsync(HistoryClearScope? scope = null)
    {
        if (Preview is { } preview)
        {
            Preview = new HistoryPage(preview.Entries.Where(e => e.Kind != HistoryKind.Device
                || (scope?.App is { } app && e.App != app) || (scope?.Source is { } source && e.Source != source)).ToList(), preview.More);
            return true;
        }
        if (MobileHost.Client is not { IsConnected: true } client) return false;
        try
        {
            var r = scope is null || scope.IsAll
                ? await client.RequestAsync(MobileBridge.OpHistoryClear)
                : await client.RequestAsync(MobileBridge.OpHistoryClear, scope);
            return (bool?)r["ok"] == true;
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Deletes one relayed notification (M-81). False when the Agent refused or was unreachable.</summary>
    public static async Task<bool> DeleteAsync(long id)
    {
        if (Preview is { } preview)
        {
            Preview = new HistoryPage(preview.Entries.Where(e => !(e.Kind == HistoryKind.Device && e.Id == id)).ToList(), preview.More);
            return true;
        }
        if (MobileHost.Client is not { IsConnected: true } client) return false;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpHistoryDelete, new HistoryDeleteRequest(id));
            return (bool?)r["ok"] == true;
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Re-counts unread entries (newer than <see cref="LastSeen"/>). Call on the UI thread.</summary>
    public static async Task RefreshUnreadAsync()
    {
        var seen = LastSeen();
        var (page, failure) = await ListAsync(null, CountWindow);
        if (failure != Failure.None || page is null) return; // keep the last count
        var unread = page.Entries.Count(e => e.At > seen);
        // The Agent may shorten a page of long entries; if all of it is unread there are more ("99+").
        if (page.More && unread == page.Entries.Count) unread = Math.Max(unread, CountWindow);
        SetUnread(unread);
    }

    private static void SetUnread(int n)
    {
        if (n == Unread) return;
        Unread = n;
        UnreadChanged?.Invoke(n);
    }
}
