using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// "Files received" as a real Windows toast above the clock (DECISIONS M-67), not a tray balloon: a single picture shows in
/// it, and it has [Open] and [Open folder]. Windows decides when it pops up — with Do not disturb (Focus assist) on it goes
/// straight to the notification center, quietly.
/// <para>
/// The toast is the tray's own (app id <see cref="Aumid"/>, registered for this user with its name and icon). Its clicks are
/// <c>homebase://files/…</c> links, registered for this user to run <c>Homebase.Tray.exe --toast &lt;link&gt;</c>, so they work
/// from the notification center too, long after the popup. A link only opens a received file by its plain name inside
/// Downloads\Homebase — or that folder; a program or script is shown in the folder rather than run.
/// </para>
/// </summary>
internal static class ReceivedToast
{
    public const string Aumid = "Homebase.Tray";
    public const string Scheme = "homebase";
    public const string Argument = "--toast";
    /// <summary>Windows shows toast images up to 3 MB.</summary>
    private const long MaxPictureBytes = 3 * 1024 * 1024;

    private static readonly string[] Runnable =
        [".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".cpl", ".reg", ".jar"];

    private static bool _registered;

    /// <summary>Shows the toast; false when Windows wouldn't (the caller falls back to a balloon).</summary>
    public static bool Show(string title, string body, IReadOnlyList<string> paths, bool picture)
    {
        if (paths.Count == 0) return false;
        try
        {
            Register();
            var xml = new XmlDocument();
            xml.LoadXml(Xml(title, body, paths, picture));
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(xml) { Group = "files" });
            return true;
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Files", $"The Windows toast didn't show ({ex.GetType().Name}: {ex.Message}); a balloon instead.");
            return false;
        }
    }

    /// <summary>
    /// The toast: a click shows the file in Explorer (several: opens the folder); [Open] opens a single file; [Open folder].
    /// A single picture small enough is shown in it.
    /// </summary>
    internal static string Xml(string title, string body, IReadOnlyList<string> paths, bool picture)
    {
        var one = paths.Count == 1;
        var first = Path.GetFileName(paths[0]);
        var xml = new StringBuilder();
        xml.Append($"<toast launch=\"{Attr(one ? Link("show", first) : Link("folder"))}\" activationType=\"protocol\">");
        xml.Append("<visual><binding template=\"ToastGeneric\">");
        xml.Append($"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>");
        if (one && picture && ShowsAsImage(paths[0]))
            xml.Append($"<image placement=\"hero\" src=\"{Attr(new Uri(paths[0]).AbsoluteUri)}\"/>");
        xml.Append("</binding></visual><actions>");
        if (one) xml.Append($"<action content=\"{Attr(L.ToastOpen)}\" activationType=\"protocol\" arguments=\"{Attr(Link("open", first))}\"/>");
        xml.Append($"<action content=\"{Attr(L.CenterOpenFolder)}\" activationType=\"protocol\" arguments=\"{Attr(Link("folder"))}\"/>");
        xml.Append("</actions></toast>");
        return xml.ToString();
    }

    /// <summary>
    /// A phone asked this PC to restart (M-69): a toast with the time and [Cancel] — which works from the notification center
    /// too. Replaced by the next one; taken away when it is called off.
    /// </summary>
    public static bool ShowRestart(DateTimeOffset at, string by)
    {
        try
        {
            Register();
            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast activationType=\"protocol\" launch=\"{Attr(Scheme + "://commands/cancel-restart")}\">" +
                $"<visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(L.RestartToastTitle(at.ToLocalTime()))}</text>" +
                $"<text>{SecurityElement.Escape(L.RestartToastBody(by))}</text></binding></visual>" +
                $"<actions><action content=\"{Attr(L.Cancel)}\" activationType=\"protocol\" arguments=\"{Attr(Scheme + "://commands/cancel-restart")}\"/></actions></toast>");
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(xml) { Tag = "restart", Group = "commands" });
            return true;
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Commands", $"The restart toast didn't show ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }

    public static void HideRestart()
    {
        try { ToastNotificationManager.History.Remove("restart", "commands", Aumid); }
        catch (Exception ex) { TrayLog.Warn("Commands", $"The restart toast stayed: {ex.GetType().Name}"); }
    }

    /// <summary>For the preview: whether Windows lets this app show toasts, and how many of ours the notification center holds.</summary>
    internal static string Diagnose()
    {
        try
        {
            return $"setting={ToastNotificationManager.CreateToastNotifier(Aumid).Setting}, " +
                $"in notification center={ToastNotificationManager.History.GetHistory(Aumid).Count}";
        }
        catch (Exception ex) { return $"unknown ({ex.GetType().Name}: {ex.Message})"; }
    }

    internal static string Link(string action, string? name = null) =>
        $"{Scheme}://files/{action}" + (name is null ? "" : "?name=" + Uri.EscapeDataString(name));

    private static string Attr(string value) => SecurityElement.Escape(value) ?? "";

    private static bool ShowsAsImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxPictureBytes
                && info.Extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// <c>Homebase.Tray.exe --toast &lt;link&gt;</c>: what a click on the toast asked for. Anything not ours, or naming a file
    /// that isn't a received one, at most opens the received folder.
    /// </summary>
    public static void Handle(string link)
    {
        // [Cancel] on the restart countdown (M-69): calls it off with this user's rights; the Agent notices and tells the phone.
        if (Uri.TryCreate(link, UriKind.Absolute, out var command) && command.Scheme == Scheme && command.Host == "commands"
            && command.AbsolutePath.Trim('/') == "cancel-restart")
        {
            try
            {
                using var abort = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/a")
                    { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                TrayLog.Warn("Commands", $"Calling off the restart failed: {ex.Message}");
            }
            return;
        }
        var folder = FileReceiver.Folder();
        if (Parse(link) is not { } parsed) return;
        var (action, name) = parsed;
        var path = Received(folder, name);
        try
        {
            switch (action)
            {
                case "open" when path is not null && !IsRunnable(path):
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    break;
                case "open" or "show" when path is not null:
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                    break;
                default:
                    Directory.CreateDirectory(folder);
                    Process.Start("explorer.exe", $"\"{folder}\"");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TrayLog.Warn("Files", $"A toast click couldn't open {action}: {ex.Message}");
        }
    }

    /// <summary>The action and file name of one of our links; null for anything else.</summary>
    internal static (string Action, string? Name)? Parse(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Scheme || uri.Host != "files") return null;
        var action = uri.AbsolutePath.Trim('/');
        if (action is not ("open" or "show" or "folder")) return null;
        string? name = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith("name=", StringComparison.Ordinal)) name = Uri.UnescapeDataString(part[5..]);
        return (action, name);
    }

    /// <summary>The received file by that plain name, directly in <paramref name="folder"/> and there now; else null.</summary>
    internal static string? Received(string folder, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or "..")
            return null;
        var root = Path.GetFullPath(folder).TrimEnd('\\');
        var path = Path.GetFullPath(Path.Combine(root, name));
        return string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    internal static bool IsRunnable(string path) => Runnable.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// For this Windows user: the app id toasts show as (name and icon), and the <c>homebase:</c> links pointing at this tray.
    /// Kept in step with where it is installed; the uninstaller removes them.
    /// </summary>
    public static void Prepare()
    {
        try { Register(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            TrayLog.Warn("Files", $"Could not register the toast links: {ex.Message}");
        }
    }

    private static void Register()
    {
        if (_registered) return;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The tray's path is unknown.");
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
        {
            app.SetValue("DisplayName", "Homebase");
            if (IconFile() is { } icon) app.SetValue("IconUri", icon);
        }
        using (var scheme = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme))
        {
            scheme.SetValue("", "URL:Homebase");
            scheme.SetValue("URL Protocol", "");
            using var command = scheme.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" {Argument} \"%1\"");
        }
        _registered = true;
    }

    /// <summary>The Homebase icon as a PNG for the toast's app icon, written once next to the tray's other files.</summary>
    private static string? IconFile()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "toast-icon.png");
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var uri = new Uri("pack://application:,,,/Homebase.Tray;component/Resources/homebase.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
            using var icon = new System.Drawing.Icon(stream, 256, 256);
            using var bitmap = icon.ToBitmap();
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
