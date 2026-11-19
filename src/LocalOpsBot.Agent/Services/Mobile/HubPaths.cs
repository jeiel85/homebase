using System.Net;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// The paths this PC keeps for a PC it is linked to (DECISIONS M-29, M-59). The list is the other PC's own
/// (<c>GET /api/v1/endpoints</c>, or what it names in its presence here), with two rules on top:
/// <list type="bullet">
/// <item>A Tailscale path stays while the fresh list has none: a tailnet address and MagicDNS name do not
/// change, so a list read while that PC's Tailscale was down must not lose them. LAN addresses and Quick
/// Tunnel names do change, so those follow the fresh list.</item>
/// <item>Paths a PC names in its presence are taken one by one and only of the kinds it can really have,
/// since this PC sends its token for that PC along them.</item>
/// </list>
/// </summary>
public static class HubPaths
{
    /// <summary>The most paths kept from one presence report.</summary>
    public const int MaxAdvertised = 8;

    /// <summary>
    /// <paramref name="fresh"/> (the PC's current list) plus the Tailscale paths of <paramref name="known"/>
    /// when <paramref name="fresh"/> has none, put after the LAN paths as the PC itself orders them.
    /// </summary>
    public static IReadOnlyList<MobileEndpoint> Refresh(IReadOnlyList<MobileEndpoint> fresh, IReadOnlyList<MobileEndpoint> known)
    {
        if (fresh.Any(e => e.Transport == MobileTransport.Tailscale)) return fresh.ToList();
        var kept = known.Where(e => e.Transport == MobileTransport.Tailscale).Distinct().ToList();
        if (kept.Count == 0) return fresh.ToList();
        return fresh.Where(e => e.Transport == MobileTransport.Lan)
            .Concat(kept)
            .Concat(fresh.Where(e => e.Transport != MobileTransport.Lan))
            .ToList();
    }

    /// <summary>
    /// The paths a linked PC named for itself in its presence that this PC may use; empty when none.
    /// LAN: a private IPv4 address over TLS with a certificate pin. Tailscale: a tailnet address or a
    /// MagicDNS name (plain HTTP, so never anywhere else). Cloudflare: a host name over TLS. Never loopback
    /// (a remote PC's 127.0.0.1 is this PC's own Agent) — but in tests, where every PC is on loopback.
    /// </summary>
    public static IReadOnlyList<MobileEndpoint> Usable(IReadOnlyList<MobileEndpoint>? advertised, bool allowLoopback = false) =>
        (advertised ?? Array.Empty<MobileEndpoint>()).Where(e => IsUsable(e, allowLoopback)).Distinct().Take(MaxAdvertised).ToList();

    private static bool IsUsable(MobileEndpoint? e, bool allowLoopback)
    {
        if (e is null || e.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(e.Host) || e.Host.Length > 253) return false;
        var ip = IPAddress.TryParse(e.Host, out var parsed) ? parsed : null;
        return e.Transport switch
        {
            MobileTransport.Lan => e.Tls && ip is not null && LanAddressResolver.IsPrivateV4(ip) && IsPin(e.CertSha256),
            MobileTransport.Tailscale => !e.Tls && (ip is not null ? TailscaleAddressResolver.IsTailnetV4(ip) : IsMagicDns(e.Host)),
            MobileTransport.Cloudflare => e.Tls && ip is null && IsDnsName(e.Host),
            MobileTransport.Loopback => allowLoopback && ip is not null && IPAddress.IsLoopback(ip),
            _ => false,
        };
    }

    private static bool IsMagicDns(string host) =>
        IsDnsName(host) && host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);

    private static bool IsDnsName(string host) =>
        Uri.CheckHostName(host) == UriHostNameType.Dns && host.Contains('.') && !host.EndsWith('.');

    private static bool IsPin(string? pin) =>
        pin is { Length: 64 } && pin.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
