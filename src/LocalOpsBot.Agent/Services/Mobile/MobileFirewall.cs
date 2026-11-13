using System.Diagnostics;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Keeps the inbound Windows Firewall rules in step with the <c>mobile</c> config. Every rule
/// allows TCP on the mobile port for the Agent executable only:
/// <list type="bullet">
/// <item><see cref="TailscaleRuleName"/>: from 100.64.0.0/10, any profile (M-1, M-14).</item>
/// <item><see cref="LanRuleName"/>: from the local subnet, Private profile only — a café or
/// airport Wi-Fi (Public) never gets the port (M-15).</item>
/// </list>
/// The Cloudflare Tunnel needs no rule: its connector dials out and reaches the API on loopback.
/// The Agent (LocalSystem) owns the rules so they always name the running exe; uninstall removes them.
/// </summary>
public static class MobileFirewall
{
    public const string TailscaleRuleName = "Homebase Mobile (Tailscale)";
    public const string LanRuleName = "Homebase Mobile (LAN)";

    public static void Apply(MobileOptions options, ILogger logger)
    {
        var markerDir = Infrastructure.Mobile.MobileDeviceStore.DefaultDirectory;
        // Only touch the firewall when the feature is on or was ever used — no netsh spawn on every boot otherwise.
        if (!options.Enabled && !Directory.Exists(markerDir)) return;

        var exe = Environment.ProcessPath;
        Ensure(TailscaleRuleName, options.Enabled && options.Tailscale, exe, options.Port,
            "remoteip=100.64.0.0/10 profile=any", logger);
        Ensure(LanRuleName, options.Enabled && options.Lan, exe, options.Port,
            "remoteip=localsubnet profile=private", logger);
    }

    private static void Ensure(string rule, bool wanted, string? exe, int port, string scope, ILogger logger)
    {
        Netsh($"advfirewall firewall delete rule name=\"{rule}\"", logger, quietFailure: true);
        if (!wanted) return;
        if (string.IsNullOrEmpty(exe))
        {
            logger.LogWarning("Mobile firewall rule {Rule} skipped: the Agent executable path is unknown.", rule);
            return;
        }
        Netsh($"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP " +
              $"localport={port} {scope} program=\"{exe}\"", logger, quietFailure: false);
    }

    private static void Netsh(string args, ILogger logger, bool quietFailure)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { p.Kill(); return; }
            if (p.ExitCode != 0 && !quietFailure)
                logger.LogWarning("netsh {Args} failed ({Code}): {Output}", args, p.ExitCode, output.Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (!quietFailure) logger.LogWarning(ex, "netsh could not be started.");
        }
    }
}
