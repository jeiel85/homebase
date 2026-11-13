using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>A private LAN address of this PC and its subnet prefix length.</summary>
public sealed record LanAddress(IPAddress Address, int PrefixLength);

/// <summary>
/// Finds this PC's private LAN addresses for the opt-in LAN path (<c>mobile:lan</c>, DECISIONS M-15).
/// Only adapters with an IPv4 default gateway count: that keeps Hyper-V/WSL/VMware host-only
/// switches (also private ranges, but gateway-less) out of the QR and the bind list.
/// </summary>
public class LanAddressResolver
{
    private readonly ILogger<LanAddressResolver> _logger;

    public LanAddressResolver(ILogger<LanAddressResolver> logger) => _logger = logger;

    /// <summary>RFC 1918: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16.</summary>
    public static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Whether <paramref name="peer"/> is inside one of <paramref name="local"/>'s subnets.</summary>
    public static bool InSameSubnet(IPAddress peer, IEnumerable<LanAddress> local)
    {
        if (peer.AddressFamily != AddressFamily.InterNetwork) return false;
        var p = ToUInt(peer);
        foreach (var l in local)
        {
            if (l.PrefixLength is < 1 or > 32) continue;
            var mask = l.PrefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> l.PrefixLength);
            if ((p & mask) == (ToUInt(l.Address) & mask)) return true;
        }
        return false;
    }

    /// <summary>Virtual so tests can supply the address set.</summary>
    public virtual IReadOnlyList<LanAddress> FindLanAddresses()
    {
        try
        {
            var result = new List<LanAddress>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                if (!hasGateway) continue;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (IsPrivateV4(ua.Address) && !TailscaleAddressResolver.IsTailnetV4(ua.Address) &&
                        result.All(r => !r.Address.Equals(ua.Address)))
                        result.Add(new LanAddress(ua.Address, ua.PrefixLength));
                }
            }
            return result;
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate network interfaces.");
            return Array.Empty<LanAddress>();
        }
    }

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
}
