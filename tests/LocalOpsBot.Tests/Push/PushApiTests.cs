using System.Text;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using LocalOpsBot.Tests.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Push;

public class PushApiTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly FakeSampler _sampler = new();
    private readonly PairingService _pairing;
    private readonly MobileApiServer _api;

    public PushApiTests()
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(_sampler, options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        _pairing = new PairingService(_store, _time);
        _api = new MobileApiServer(hub, _pairing, _store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options,
            NullLogger<MobileApiServer>.Instance);
    }

    private static HttpRequestHead Req(string method, string? token) =>
        new(method, "/api/v1/push", token is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token });

    private static byte[] Body(PushSubscriptionRequest r) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, MobileProtocol.Json));

    private static PushSubscriptionRequest Sub(string endpoint = "https://ntfy.sh/upAbc?up=1") =>
        new(1, endpoint, WebPushEncryptionTests.UaPublic, WebPushEncryptionTests.AuthSecret);

    private string Pair()
    {
        var (code, _) = _pairing.Begin();
        return _pairing.TryPair(code, "Galaxy", null).Token!;
    }

    [Fact]
    public async Task Register_update_and_unregister()
    {
        var token = Pair();

        var (status, json) = await _api.HandleRestAsync(Req("POST", token), Body(Sub()), default);
        Assert.Equal(200, status);
        var res = JsonSerializer.Deserialize<PushSubscriptionResponse>(json, MobileProtocol.Json)!;
        Assert.True(res.Registered);
        Assert.Equal("https://ntfy.sh/upAbc?up=1", Assert.Single(_store.PushTargets()).Subscription.Endpoint);

        await _api.HandleRestAsync(Req("POST", token), Body(Sub("https://ntfy.sh/upNew")), default);
        Assert.Equal("https://ntfy.sh/upNew", Assert.Single(_store.PushTargets()).Subscription.Endpoint);

        (status, json) = await _api.HandleRestAsync(Req("DELETE", token), null, default);
        Assert.Equal(200, status);
        Assert.False(JsonSerializer.Deserialize<PushSubscriptionResponse>(json, MobileProtocol.Json)!.Registered);
        Assert.Empty(_store.PushTargets());
        Assert.False(_sampler.Started); // registering never starts live collection (M-13)
    }

    [Fact]
    public async Task Requires_a_paired_token()
    {
        Assert.Equal(401, (await _api.HandleRestAsync(Req("POST", null), Body(Sub()), default)).Status);
        Assert.Equal(401, (await _api.HandleRestAsync(Req("POST", "nope"), Body(Sub()), default)).Status);
        Assert.Equal(401, (await _api.HandleRestAsync(Req("DELETE", "nope"), null, default)).Status);
        Assert.Equal(405, (await _api.HandleRestAsync(Req("GET", Pair()), null, default)).Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema\":1,\"endpoint\":\"http://ntfy.sh/up\",\"p256dh\":\"x\",\"auth\":\"y\"}")]
    [InlineData("{\"schema\":2,\"endpoint\":\"https://ntfy.sh/up\",\"p256dh\":\"x\",\"auth\":\"y\"}")]
    public async Task Bad_subscriptions_are_400(string body)
    {
        var (status, json) = await _api.HandleRestAsync(Req("POST", Pair()), Encoding.UTF8.GetBytes(body), default);
        Assert.Equal(400, status);
        Assert.Contains("invalid_subscription", json);
        Assert.Empty(_store.PushTargets());
    }

    [Fact]
    public async Task Revoking_the_device_removes_its_subscription()
    {
        var token = Pair();
        await _api.HandleRestAsync(Req("POST", token), Body(Sub()), default);
        var device = Assert.Single(_store.List());

        Assert.True(_store.Revoke(device.DeviceId));

        Assert.Empty(_store.PushTargets());
        Assert.Equal(401, (await _api.HandleRestAsync(Req("POST", token), Body(Sub()), default)).Status);
    }
}

public class PushSubscriptionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-push-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Subscription_survives_reload_and_goes_away_with_revoke()
    {
        var store = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        var d = store.Add("Phone", null, PairingService.HashToken("t"));
        var sub = new PushSubscription("https://ntfy.sh/upX", WebPushEncryptionTests.UaPublic, WebPushEncryptionTests.AuthSecret, _time.GetUtcNow());
        Assert.True(store.SetPushSubscription(d.DeviceId, sub));
        Assert.False(store.SetPushSubscription("unknown", sub));

        var reloaded = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        Assert.Equal(sub, Assert.Single(reloaded.PushTargets()).Subscription);

        Assert.True(reloaded.Revoke(d.DeviceId));
        Assert.Empty(new MobileDeviceStore(_dir, _time, restrictAcl: false).PushTargets());
    }

    [Fact]
    public void State_written_before_push_support_still_loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "state.json"),
            "{\"pcId\":\"abc\",\"devices\":[{\"deviceId\":\"d1\",\"name\":\"P\",\"tokenSha256\":\"00\",\"pairedAt\":\"2026-11-13T00:00:00+00:00\"}]}");
        var store = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        Assert.Single(store.List());
        Assert.Empty(store.PushTargets());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
