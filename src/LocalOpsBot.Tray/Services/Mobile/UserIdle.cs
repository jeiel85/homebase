using System.Runtime.InteropServices;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>How long the signed-in user has not touched the keyboard or mouse (GetLastInputInfo).</summary>
internal static class UserIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Zero when Windows cannot tell, so the caller treats the user as present.</summary>
    public static TimeSpan Duration()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Both are milliseconds since boot in 32 bits; unsigned subtraction survives the 49-day wrap.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }
}
