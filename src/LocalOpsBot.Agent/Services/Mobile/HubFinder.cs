using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>A PC found from a connection code: its identity and the path that answered.</summary>
public sealed record FoundHub(PcInfo Info, MobileEndpoint Endpoint);

/// <summary>
/// Finds the PC a 24-word connection code names (DECISIONS M-30) by asking <c>GET /api/v1/info</c>
/// of every online tailnet peer and every host in this PC's own LAN /24, all at once with short
/// timeouts. A LAN host is only spoken to over TLS whose certificate SHA-256 starts with the
/// code's 8-byte prefix; a PC answers as the one only when its <c>pcId</c> hashes to the code's tag.
/// LAN matches win over tailnet ones (faster, and what a phone would pick too).
/// </summary>
public sealed class HubFinder
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    private const int Parallel = 64;
    private const int MaxLanHosts = 254;

    private readonly Func<ConnectionCodeData, IReadOnlyList<MobileEndpoint>> _candidates;
    private readonly ILogger _logger;

    public HubFinder(Func<ConnectionCodeData, IReadOnlyList<MobileEndpoint>> candidates, ILogger logger)
    {
        _candidates = candidates;
        _logger = logger;
    }

    /// <summary>Candidates from the tailnet and this PC's LAN subnets.</summary>
    public static Func<ConnectionCodeData, IReadOnlyList<MobileEndpoint>> NetworkCandidates(
        TailscaleAddressResolver tailscale, LanAddressResolver lan) => code =>
    {
        var list = new List<MobileEndpoint>();
        if (code.LanPinPrefix is not null)
        {
            foreach (var local in lan.FindLanAddresses())
                foreach (var host in LanHosts(local))
                    list.Add(new MobileEndpoint(MobileTransport.Lan, host.ToString(), code.Port, true));
        }
        var self = tailscale.FindTailnetAddresses();
        foreach (var peer in tailscale.FindPeerAddresses().Where(p => !self.Contains(p)))
            list.Add(new MobileEndpoint(MobileTransport.Tailscale, peer.ToString(), code.Port, false));
        return list.DistinctBy(e => (e.Transport, e.Host)).ToList();
    };

    /// <summary>Other hosts of <paramref name="local"/>'s subnet, at most its /24.</summary>
    internal static IEnumerable<IPAddress> LanHosts(LanAddress local)
    {
        var prefix = Math.Max(local.PrefixLength, 24);
        if (prefix >= 31) yield break;
        var self = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(local.Address.GetAddressBytes());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = self & mask;
        var size = 1u << (32 - prefix);
        for (var i = 1u; i < size - 1 && i <= MaxLanHosts; i++)
        {
            var ip = network + i;
            if (ip == self) continue;
            var bytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, ip);
            yield return new IPAddress(bytes);
        }
    }

    public async Task<FoundHub?> FindAsync(ConnectionCodeData code, CancellationToken ct)
    {
        var candidates = _candidates(code);
        _logger.LogInformation("Looking for the PC of a connection code among {Count} addresses.", candidates.Count);
        var found = new ConcurrentBag<FoundHub>();
        using var gate = new SemaphoreSlim(Parallel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probes = candidates.Select(async ep =>
        {
            try { await gate.WaitAsync(stop.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
            try
            {
                if (await ProbeAsync(code, ep, stop.Token) is { } hit)
                {
                    found.Add(hit);
                    if (hit.Endpoint.Transport == MobileTransport.Lan) stop.Cancel(); // nothing beats a LAN hit
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(probes); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();

        return found.OrderBy(f => f.Endpoint.Transport == MobileTransport.Lan ? 0 : 1).FirstOrDefault();
    }

    private static async Task<FoundHub?> ProbeAsync(ConnectionCodeData code, MobileEndpoint ep, CancellationToken ct)
    {
        string? pin = null;
        using var handler = new SocketsHttpHandler { ConnectTimeout = ConnectTimeout, UseProxy = false };
        if (ep.Tls)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                var hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();
                if (!code.MatchesPin(hex)) return false;
                pin = hex;
                return true;
            };
        }
        using var http = new HttpClient(handler) { Timeout = ProbeTimeout };
        try
        {
            var json = await http.GetStringAsync(HubClient.Url(ep, "/api/v1/info"), ct);
            var info = JsonSerializer.Deserialize<PcInfo>(json, MobileProtocol.Json);
            if (info is null || !code.MatchesPcId(info.PcId)) return null;
            return new FoundHub(info, ep with { CertSha256 = ep.Tls ? pin : null });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or SocketException or IOException)
        {
            return null;
        }
    }
}
