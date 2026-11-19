using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Explorer's "Send to › Homebase" (DECISIONS M-66): a shortcut in this user's <c>shell:sendto</c> folder that runs
/// <c>Homebase.Tray.exe --send &lt;paths&gt;</c>. The tray keeps it in step at start (and when the switch in Settings
/// changes); the uninstaller removes it from every profile. Windows 11 lists it under "Show more options".
/// </summary>
internal static class SendToShortcut
{
    public const string Argument = "--send";
    private const string FileName = "Homebase.lnk";

    /// <summary>The shortcut in this user's Send To folder.</summary>
    public static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), FileName);

    /// <summary>
    /// Puts the shortcut there (or points it at <paramref name="exePath"/> again) when <paramref name="enabled"/>, else
    /// takes ours away. A shortcut by that name that runs something else is left alone.
    /// </summary>
    public static void Sync(bool enabled, string exePath) => Sync(enabled, exePath, LinkPath);

    internal static void Sync(bool enabled, string exePath, string linkPath)
    {
        try
        {
            var current = File.Exists(linkPath) ? TargetOf(linkPath) : null;
            var ours = current is not null && IsHomebase(current);
            if (!enabled)
            {
                if (ours) File.Delete(linkPath);
                return;
            }
            if (current is not null && !ours) return; // someone else's "Homebase" shortcut
            if (current is not null && string.Equals(Path.GetFullPath(current), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Create(linkPath, exePath);
            TrayLog.Info("Files", $"Explorer's Send to menu has Homebase ({linkPath}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            TrayLog.Warn("Files", $"Could not update the Send to shortcut: {ex.Message}");
        }
    }

    /// <summary>A shortcut to the Homebase tray, wherever it is installed.</summary>
    private static bool IsHomebase(string target) =>
        string.Equals(Path.GetFileName(target), "Homebase.Tray.exe", StringComparison.OrdinalIgnoreCase);

    internal static void Create(string linkPath, string exePath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exePath);
            link.SetArguments(Argument);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetIconLocation(exePath, 0);
            link.SetDescription("Homebase");
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    internal static string? TargetOf(string linkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(linkPath, 0); // STGM_READ
            var path = new StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.Length == 0 ? null : path.ToString();
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
