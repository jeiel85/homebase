using System.Runtime.InteropServices;
using System.Text;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// The windows a person would see in Alt-Tab: visible, unowned, not tool windows, not cloaked
/// (UWP placeholders / other virtual desktops), with a title. One EnumWindows pass — about a
/// millisecond — and only while a phone is watching.
/// </summary>
internal static class WindowEnumerator
{
    private const int MaxTitleLength = 300;

    public static IReadOnlyList<SessionWindow> Enumerate()
    {
        var foreground = GetForegroundWindow();
        var result = new List<SessionWindow>(64);
        var title = new StringBuilder(MaxTitleLength + 1);
        var cls = new StringBuilder(64);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            title.Clear();
            if (GetWindowText(hwnd, title, title.Capacity) <= 0) return true;
            cls.Clear();
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "Shell_TrayWnd" or "WorkerW") return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            result.Add(new SessionWindow(hwnd.ToInt64(), (int)pid, title.ToString(), hwnd == foreground));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
