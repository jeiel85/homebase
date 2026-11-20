using System.Runtime.InteropServices;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Brings the tray back when it ends unexpectedly. Nothing else does: the tray starts at sign-in (HKCU Run)
/// and after an update, so one that crashed or hung stayed gone until the next sign-in, and with it the
/// phone's live view, file sharing, remote commands and notification forwarding. The usual cause was not the
/// tray itself: when Windows runs out of virtual memory (emulators, build daemons, many apps), any process
/// can fail a small allocation, and the tray (45 MB) was one of them.
/// <para>
/// Windows Error Reporting restarts a registered process that crashed or stopped responding, once it has run
/// for 60 seconds (so a tray that fails at start does not loop). A clean exit (Quit in the menu) and the
/// updater's <c>Stop-Process</c> are not crashes, so they do not restart it. Restarts for Windows Update and
/// installers are left out: sign-in and the updater already start the tray then.
/// </para>
/// <para>
/// The API documentation still says a crashed application is not restarted without the user's consent. On
/// Windows 11 with Windows Error Reporting at its defaults (no policy, no <c>DontShowUI</c>) it was restarted
/// unattended, about seven seconds after the crash, with nobody at the PC (checked 2026-11-20). Where Windows
/// Error Reporting is turned off by policy, nothing restarts it and the tray is back at the next sign-in, as before.
/// </para>
/// </summary>
internal static class CrashRestart
{
    /// <summary>The argument Windows starts the tray with after a crash. It carries no paths to send.</summary>
    public const string Argument = "--restarted-after-crash";

    private const int RestartNoPatch = 4;
    private const int RestartNoReboot = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterApplicationRestart(string pwzCommandline, int dwFlags);

    /// <summary>Asks Windows to start this tray again if it crashes or hangs; says so in tray.log when it did.</summary>
    public static void Register(IReadOnlyList<string> args)
    {
        if (args.Contains(Argument))
            TrayLog.Warn("App", "Started again by Windows after the tray ended unexpectedly");

        var hr = RegisterApplicationRestart(Argument, RestartNoPatch | RestartNoReboot);
        if (hr != 0)
            TrayLog.Warn("App", $"The tray could not ask Windows to restart it after a crash (0x{hr:X8}); it will come back at the next sign-in");
    }
}
