using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using Xunit;

namespace LocalOpsBot.Tests.Push;

/// <summary>Hardening #25: a path list too long for one push is cut down instead of failing every retry.</summary>
public class PushEndpointsTests
{
    private const string PcId = "0123456789abcdef0123456789abcdef";
    private static readonly string TokenHash = new('a', 64);
    private static readonly DateTimeOffset Ts = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static MobileEndpoint LanPath(int i) =>
        new(MobileTransport.Lan, $"192.168.{i}.{100 + i}", 47820, true, new string('b', 64));

    private static IReadOnlyList<MobileEndpoint> ManyPaths()
    {
        // The order MobileApiServer.Endpoints() uses: LAN, Tailscale (MagicDNS, then IPs), Cloudflare.
        var list = Enumerable.Range(1, 60).Select(LanPath).ToList();
        list.Add(new MobileEndpoint(MobileTransport.Tailscale, "home-pc.tail1234.ts.net", 47820, false));
        list.Add(new MobileEndpoint(MobileTransport.Tailscale, "100.123.134.41", 47820, false));
        list.Add(new MobileEndpoint(MobileTransport.Cloudflare, "transport-basin-issue-into.trycloudflare.com", 443, true));
        return list;
    }

    private static PushEndpointsData Decode(byte[] plaintext)
    {
        var envelope = JsonSerializer.Deserialize<PushEndpointsEnvelope>(plaintext, MobileProtocol.Json)!;
        return JsonSerializer.Deserialize<PushEndpointsData>(Base64Url.Decode(envelope.Data), MobileProtocol.Json)!;
    }

    [Fact]
    public void A_short_list_is_sent_whole()
    {
        var paths = ManyPaths().Skip(58).ToList();
        Assert.Same(paths, PushEndpoints.FitToOnePush(paths));
        Assert.Equal(paths, Decode(PushEndpoints.Serialize(PcId, paths, Ts, TokenHash)).Endpoints);
    }

    [Fact]
    public void A_long_list_is_cut_to_fit_keeping_the_first_path_of_each_transport()
    {
        var all = ManyPaths();

        var bytes = PushEndpoints.Serialize(PcId, all, Ts, TokenHash);

        Assert.True(bytes.Length <= WebPushEncryption.MaxPlaintextBytes);
        var sent = Decode(bytes).Endpoints;
        Assert.InRange(sent.Count, 3, all.Count - 1);
        Assert.Equal(PushEndpoints.FitToOnePush(all), sent);
        // The preferred path of every transport survives...
        Assert.Contains(all[0], sent);
        Assert.Contains(all[60], sent);
        Assert.Contains(all[62], sent);
        // ...and the original preference order is kept.
        var positions = sent.Select(e => all.ToList().IndexOf(e)).ToList();
        Assert.Equal(positions.Order(), positions);
    }
}
