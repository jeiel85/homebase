using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Carries out a remote command in this user's session (DECISIONS M-69): lock, screen off, the volume and media keys, or a
/// custom command this user registered. Restart and sleep are the Agent's.
/// </summary>
internal static class CommandRunner
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static readonly IntPtr Broadcast = new(0xFFFF);
    private const uint WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;

    /// <summary>Whether it was carried out.</summary>
    public static bool Run(string name)
    {
        try
        {
            return name switch
            {
                RemoteCommandNames.Lock => LockWorkStation(),
                // Off until the mouse or keyboard is used.
                RemoteCommandNames.ScreenOff => PostMessage(Broadcast, WmSysCommand, (IntPtr)ScMonitorPower, (IntPtr)2),
                RemoteCommandNames.Mute => Key(0xAD),
                RemoteCommandNames.VolumeDown => Key(0xAE),
                RemoteCommandNames.VolumeUp => Key(0xAF),
                RemoteCommandNames.Next => Key(0xB0),
                RemoteCommandNames.Previous => Key(0xB1),
                RemoteCommandNames.PlayPause => Key(0xB3),
                _ when RemoteCommandNames.IsCustom(name) => RunCustom(name[RemoteCommandNames.CustomPrefix.Length..]),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            TrayLog.Warn("Commands", $"Remote command {name} failed: {ex.Message}");
            return false;
        }
    }

    private static bool Key(byte vk)
    {
        keybd_event(vk, 0, KeyExtended, UIntPtr.Zero);
        keybd_event(vk, 0, KeyExtended | KeyUp, UIntPtr.Zero);
        return true;
    }

    /// <summary>A registered program with its fixed arguments, with this user's rights; nothing from the phone reaches it.</summary>
    private static bool RunCustom(string id)
    {
        if (CustomCommands.Find(id) is not { } command) return false;
        var start = new ProcessStartInfo(command.Program)
        {
            Arguments = command.Arguments ?? "",
            UseShellExecute = true,
            WorkingDirectory = command.WorkingDirectory is { Length: > 0 } dir && Directory.Exists(dir)
                ? dir
                : Path.GetDirectoryName(command.Program) is { Length: > 0 } own && Directory.Exists(own) ? own : "",
        };
        using var process = Process.Start(start);
        TrayLog.Info("Commands", $"Custom command '{command.Name}' started.");
        return true;
    }
}
