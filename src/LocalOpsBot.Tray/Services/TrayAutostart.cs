using Microsoft.Win32;

namespace LocalOpsBot.Tray.Services;

internal enum AutostartState { Enabled, Disabled, NotRegistered, Unknown }

/// <summary>
/// Whether the tray starts at sign-in. The installer registers <c>HKCU\…\CurrentVersion\Run</c> value
/// <c>Homebase.Tray</c> (setup.iss, setup.ps1, register-startup-tray.ps1). Turning the app off in
/// Task Manager › Startup apps (or Settings › Apps › Startup) does not remove that value; Windows
/// writes a 12-byte flag of the same name under <c>Explorer\StartupApproved\Run</c> whose first byte
/// is odd when disabled (0x03, 0x07) and even when enabled (0x02, 0x06; no flag also means enabled).
/// Toast forwarding runs in the tray, so a disabled entry silently stops it after the next sign-in
/// (issue #4), which is why the dashboard and the Notifications tab warn about it.
/// </summary>
internal static class TrayAutostart
{
    public const string ValueName = "Homebase.Tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static AutostartState Read()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (run?.GetValue(ValueName) is null) return AutostartState.NotRegistered;
            }
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flag && (flag[0] & 1) == 1
                ? AutostartState.Disabled
                : AutostartState.Enabled;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return AutostartState.Unknown;
        }
    }

    /// <summary>True only when the entry exists and the user turned it off (not when unknown or never registered).</summary>
    public static bool IsDisabled() => Read() == AutostartState.Disabled;
}
