using System.Diagnostics;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Keeps the inbound Windows Firewall rule in step with <c>mobile:enabled</c>: allow TCP on the
/// mobile port, for the Agent executable only, from Tailscale addresses (100.64.0.0/10) only.
/// The Agent (LocalSystem) owns the rule so it always names the running exe; uninstall removes it.
/// </summary>
public static class MobileFirewall
{
    public const string RuleName = "Homebase Mobile (Tailscale)";

    public static void Apply(MobileOptions options, ILogger logger)
    {
        var markerDir = Infrastructure.Mobile.MobileDeviceStore.DefaultDirectory;
        if (!options.Enabled)
        {
            // Only clean up when the feature was ever used — no netsh spawn on every boot otherwise.
            if (Directory.Exists(markerDir)) Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"", logger, quietFailure: true);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            logger.LogWarning("Mobile firewall rule skipped: the Agent executable path is unknown.");
            return;
        }
        Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"", logger, quietFailure: true);
        Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP " +
              $"localport={options.Port} remoteip=100.64.0.0/10 program=\"{exe}\" profile=any", logger, quietFailure: false);
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
