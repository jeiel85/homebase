using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// The optional connectors behind the phone paths: detects whether Tailscale and cloudflared are
/// installed, installs them with winget on request, and tells whether Windows treats the LAN
/// adapter as a Public network (the LAN firewall rule is Private-only, DECISIONS M-15).
/// </summary>
internal static class MobileComponents
{
    public const string TailscalePackage = "Tailscale.Tailscale";
    public const string CloudflaredPackage = "Cloudflare.cloudflared";

    public static string TailscaleExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");

    public static string TailscaleGuiExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale-ipn.exe");

    public static bool IsTailscaleInstalled() => File.Exists(TailscaleExe);

    /// <summary>Same search order as the Agent's <c>CloudflareTunnel.LocateCloudflared</c> (install folders, then PATH).</summary>
    public static bool IsCloudflaredInstalled()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            if (File.Exists(Path.Combine(Environment.GetFolderPath(folder), "cloudflared", "cloudflared.exe"))) return true;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d =>
            {
                try { return File.Exists(Path.Combine(d.Trim(), "cloudflared.exe")); }
                catch (ArgumentException) { return false; }
            });
    }

    /// <summary>
    /// Installs a package with winget (machine-wide installers raise their own UAC prompt).
    /// Returns <c>null</c> on success or a short reason.
    /// </summary>
    public static async Task<string?> InstallAsync(string packageId)
    {
        var psi = new ProcessStartInfo("winget.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "install", "--id", packageId, "--exact", "--source", "winget", "--silent",
                     "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "winget을 시작하지 못했습니다.";
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0) return null;
            var text = (await stdout + "\n" + await stderr).Trim();
            var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return $"winget 종료 코드 0x{p.ExitCode:X8}" + (last is null ? "" : $" — {last}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "winget(앱 설치 관리자)이 없습니다. Microsoft Store에서 '앱 설치 관리자'를 설치하거나 직접 설치하세요.";
        }
    }

    /// <summary>
    /// Names of connected networks Windows classifies as Public that carry one of
    /// <paramref name="lanAddresses"/>. Best effort: <c>null</c> when the Network List Manager is unavailable.
    /// </summary>
    public static IReadOnlyList<string>? PublicNetworksFor(IEnumerable<string> lanAddresses)
    {
        var wanted = lanAddresses.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>().ToList();
        if (wanted.Count == 0) return Array.Empty<string>();
        try
        {
            var adapterIds = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.GetIPProperties().UnicastAddresses.Any(u => wanted.Contains(u.Address)))
                .Select(n => Guid.TryParse(n.Id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToHashSet();

            var result = new List<string>();
            foreach (var (adapter, network) in NetworkCategory.ByAdapter())
                if (adapterIds.Contains(adapter) && network.Category == NetworkCategory.Public && !result.Contains(network.Name))
                    result.Add(network.Name);
            return result;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or NetworkInformationException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }
}
