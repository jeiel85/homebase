using System.Net;
using System.Text;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class MiniHttpAndBridgeTests
{
    [Fact]
    public async Task Head_parses_method_path_headers_and_keeps_body_bytes()
    {
        var raw = "POST /api/v1/pair?x=1 HTTP/1.1\r\nHost: a\r\nContent-Length: 4\r\nAuthorization: Bearer abc\r\n\r\nbody";
        var r = await MiniHttp.ReadHeadAsync(new MemoryStream(Encoding.ASCII.GetBytes(raw)), CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal("POST", r!.Value.Head.Method);
        Assert.Equal("/api/v1/pair", r.Value.Head.Path);
        Assert.Equal("abc", r.Value.Head.BearerToken());
        var body = await MiniHttp.ReadBodyAsync(new MemoryStream(), r.Value.Head, r.Value.Leftover, CancellationToken.None);
        Assert.Equal("body", Encoding.ASCII.GetString(body!));
    }

    [Fact]
    public async Task Oversized_or_malformed_heads_are_rejected()
    {
        var huge = "GET / HTTP/1.1\r\nX: " + new string('a', MiniHttp.MaxHeadBytes) + "\r\n\r\n";
        Assert.Null(await MiniHttp.ReadHeadAsync(new MemoryStream(Encoding.ASCII.GetBytes(huge)), CancellationToken.None));
        Assert.Null(MiniHttp.Parse("GARBAGE"));
        Assert.Null(MiniHttp.Parse("GET / HTTP/1.1\r\nno-colon-header"));
    }

    [Fact]
    public void WebSocket_accept_matches_rfc6455_example() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", MiniHttp.WebSocketAccept("dGhlIHNhbXBsZSBub25jZQ=="));

    [Fact]
    public async Task Bridge_frames_round_trip_and_reject_bad_lengths()
    {
        var ms = new MemoryStream();
        await MobileBridge.WriteFrameAsync(ms, MobileBridge.Message("status", id: "1"), CancellationToken.None);
        ms.Position = 0;
        var back = await MobileBridge.ReadFrameAsync(ms, CancellationToken.None);
        Assert.Equal("status", (string?)back!["op"]);
        Assert.Null(await MobileBridge.ReadFrameAsync(ms, CancellationToken.None)); // clean EOF

        var bad = new MemoryStream(BitConverter.GetBytes(MobileBridge.MaxFrameBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => MobileBridge.ReadFrameAsync(bad, CancellationToken.None));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.0.10", false)]
    public void Tailnet_range_is_100_64_slash_10(string ip, bool expected) =>
        Assert.Equal(expected, TailscaleAddressResolver.IsTailnetV4(IPAddress.Parse(ip)));

    [Fact]
    public void MagicDns_name_is_read_from_status_json()
    {
        Assert.Equal("home-pc.tail1234.ts.net",
            TailscaleAddressResolver.ParseSelfDnsName("{\"Self\":{\"DNSName\":\"home-pc.tail1234.ts.net.\"}}"));
        Assert.Null(TailscaleAddressResolver.ParseSelfDnsName("{\"Self\":{}}"));
    }

    [Fact]
    public void Tray_allowlist_excludes_agent_only_ops()
    {
        Assert.DoesNotContain(MobileBridge.OpWatch, MobileBridge.AllowedTrayOps);
        Assert.Contains(MobileBridge.OpSessionReport, MobileBridge.AllowedTrayOps);
    }
}
