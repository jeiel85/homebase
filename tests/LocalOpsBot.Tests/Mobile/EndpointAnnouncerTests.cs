using System.Net;
using System.Security.Cryptography;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static LocalOpsBot.Tests.Push.UnifiedPushChannelTests;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>M-23: a new path reaches phones by push, without re-pairing.</summary>
public class EndpointAnnouncerTests
{
    private static readonly MobileEndpoint Tailnet = new(MobileTransport.Tailscale, "100.123.134.41", 47820, false);
    private static readonly MobileEndpoint Lan = new(MobileTransport.Lan, "192.168.45.101", 47820, true, new string('a', 64));
    private static readonly MobileEndpoint OldTunnel = new(MobileTransport.Cloudflare, "transport-basin-issue-into.trycloudflare.com", 443, true);
    private static readonly MobileEndpoint NewTunnel = new(MobileTransport.Cloudflare, "angel-four-sets-donation.trycloudflare.com", 443, true);

    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly CapturingHandler _http = new();
    private readonly EndpointAnnouncer _announcer;
    private List<MobileEndpoint> _current = new();

    public EndpointAnnouncerTests()
    {
        AddPhone("S24", "https://ntfy.sh/upX?up=1");
        var channel = new UnifiedPushOutboundChannel(_store, new HttpClient(_http), _time, "HOME-PC");
        _announcer = new EndpointAnnouncer(() => _current, channel, _time, NullLogger<EndpointAnnouncer>.Instance);
    }

    private void AddPhone(string name, string endpoint)
    {
        var device = _store.Add(name, null, PairingService.HashToken("t-" + name)).DeviceId;
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _store.SetPushSubscription(device, new PushSubscription(endpoint,
            Base64Url.Encode(WebPushEncryption.ExportUncompressed(key)), Base64Url.Encode(RandomNumberGenerator.GetBytes(16)), _time.GetUtcNow()));
    }

    private async Task<bool> Tick() => await _announcer.TickAsync(default);

    [Fact]
    public async Task Announces_once_the_list_settles_and_not_again_while_unchanged()
    {
        _current = new() { Tailnet, Lan };
        Assert.False(await Tick()); // first sight: wait for the tunnel to come up
        _current = new() { Tailnet, Lan, NewTunnel };
        Assert.False(await Tick()); // changed since the last poll
        Assert.True(await Tick());  // stable → push
        Assert.Single(_http.Requests);
        Assert.False(await Tick());
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task A_new_quick_tunnel_address_is_pushed_again()
    {
        _current = new() { Tailnet, OldTunnel };
        await Tick();
        await Tick();
        _current = new() { Tailnet, NewTunnel };
        await Tick();
        Assert.True(await Tick());
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A_dropped_path_alone_does_not_push()
    {
        _current = new() { Tailnet, Lan };
        await Tick();
        await Tick();
        _current = new() { Tailnet }; // Wi-Fi flapped
        await Tick();
        Assert.False(await Tick());
        _current = new() { Tailnet, Lan }; // and came back: nothing the phone doesn't know
        await Tick();
        Assert.False(await Tick());
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task A_failed_push_is_retried_after_the_back_off()
    {
        _http.Next = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _current = new() { Tailnet, NewTunnel };
        await Tick();
        Assert.True(await Tick());
        Assert.False(await Tick()); // still backing off
        _http.Next = () => new HttpResponseMessage(HttpStatusCode.Created);
        _time.Advance(EndpointAnnouncer.RetryAfter);
        Assert.True(await Tick());
        Assert.False(await Tick());
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A_phone_that_missed_the_push_is_retried_alone()
    {
        AddPhone("Tab", "https://ntfy.sh/upTab?up=1");
        var tabDown = true;
        _http.ByUri = uri => uri.Contains("upTab") && tabDown
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new HttpResponseMessage(HttpStatusCode.Created);
        _current = new() { Tailnet, NewTunnel };
        await Tick();
        Assert.True(await Tick());
        Assert.Equal(2, _http.Requests.Count); // both tried; the tablet was rate limited

        Assert.False(await Tick()); // backing off
        tabDown = false;
        _time.Advance(TimeSpan.FromHours(1)); // past both the back-off and the 429 pause
        Assert.True(await Tick());
        Assert.Equal(3, _http.Requests.Count);
        Assert.Contains("upTab", _http.Requests[^1].Uri); // only the phone that missed it
        Assert.False(await Tick());
    }

    [Fact]
    public async Task A_phone_that_registers_later_gets_the_current_list()
    {
        _current = new() { Tailnet, NewTunnel };
        await Tick();
        await Tick();
        AddPhone("Tab", "https://ntfy.sh/upTab?up=1");
        Assert.True(await Tick());
        Assert.Equal(2, _http.Requests.Count);
        Assert.Contains("upTab", _http.Requests[^1].Uri);
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_registered_phone()
    {
        foreach (var t in _store.PushTargets()) _store.SetPushSubscription(t.DeviceId, null);
        _current = new() { Tailnet, NewTunnel };
        await Tick();
        Assert.False(await Tick());
        Assert.Empty(_http.Requests);
    }
}
