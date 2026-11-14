using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// Finds this PC's Tailscale address(es) so the mobile API binds to the tailnet only — never to
/// LAN or public interfaces (DECISIONS M-1).
/// </summary>
public sealed class TailscaleAddressResolver
{
    private readonly ILogger<TailscaleAddressResolver> _logger;

    public TailscaleAddressResolver(ILogger<TailscaleAddressResolver> logger) => _logger = logger;

    /// <summary>Tailscale's CGNAT range 100.64.0.0/10.</summary>
    public static bool IsTailnetV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    /// <summary>IPv4 tailnet addresses assigned to an up interface on this machine.</summary>
    public IReadOnlyList<IPAddress> FindTailnetAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(IsTailnetV4)
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate network interfaces.");
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>
    /// The MagicDNS name (e.g. <c>home-pc.tail1234.ts.net</c>) from <c>tailscale status --json</c>,
    /// or <c>null</c> when the CLI is missing or MagicDNS is off. Best effort: the IP alone works.
    /// </summary>
    public string? FindMagicDnsName()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(exe)) return null;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, "status --json --self=true --peers=false")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000)) { proc.Kill(); return null; }
            return ParseSelfDnsName(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            _logger.LogInformation("Tailscale MagicDNS name unavailable: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// IPv4 addresses of the other tailnet devices that are online, from <c>tailscale status --json</c>
    /// (connection codes look for the other PC among them, DECISIONS M-30). Empty without the CLI.
    /// </summary>
    public IReadOnlyList<IPAddress> FindPeerAddresses()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(exe)) return Array.Empty<IPAddress>();
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, "status --json")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return Array.Empty<IPAddress>();
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000)) { proc.Kill(); return Array.Empty<IPAddress>(); }
            return ParsePeerAddresses(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            _logger.LogInformation("Tailscale peers unavailable: {Message}", ex.Message);
            return Array.Empty<IPAddress>();
        }
    }

    public static IReadOnlyList<IPAddress> ParsePeerAddresses(string statusJson)
    {
        using var doc = JsonDocument.Parse(statusJson);
        if (!doc.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
            return Array.Empty<IPAddress>();
        var list = new List<IPAddress>();
        foreach (var peer in peers.EnumerateObject())
        {
            var p = peer.Value;
            if (!p.TryGetProperty("Online", out var online) || online.ValueKind != JsonValueKind.True) continue;
            if (!p.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;
            foreach (var ip in ips.EnumerateArray())
            {
                if (ip.ValueKind == JsonValueKind.String && IPAddress.TryParse(ip.GetString(), out var a) && IsTailnetV4(a))
                    list.Add(a);
            }
        }
        return list;
    }

    public static string? ParseSelfDnsName(string statusJson)
    {
        using var doc = JsonDocument.Parse(statusJson);
        if (!doc.RootElement.TryGetProperty("Self", out var self) ||
            !self.TryGetProperty("DNSName", out var dns) || dns.ValueKind != JsonValueKind.String) return null;
        var name = dns.GetString()?.TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
