using System.Text;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class MobileApiAndHubTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly FakeSampler _sampler = new();
    private readonly LiveSnapshotHub _hub;
    private readonly PairingService _pairing;
    private readonly MobileApiServer _api;

    public MobileApiAndHubTests()
    {
        var options = Options.Create(new MobileOptions { Enabled = true, LiveIntervalMs = 500 });
        _hub = new LiveSnapshotHub(_sampler, options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        _pairing = new PairingService(_store, _time);
        _api = new MobileApiServer(_hub, _pairing, _store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options,
            NullLogger<MobileApiServer>.Instance);
    }

    private static HttpRequestHead Req(string method, string path, string? token = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return new HttpRequestHead(method, path, headers);
    }

    [Fact]
    public async Task Info_is_public_and_carries_schema_and_pc_id()
    {
        var (status, json) = await _api.HandleRestAsync(Req("GET", "/api/v1/info"), null, CancellationToken.None);
        Assert.Equal(200, status);
        var info = JsonSerializer.Deserialize<PcInfo>(json, MobileProtocol.Json)!;
        Assert.Equal(MobileProtocol.Schema, info.Schema);
        Assert.Equal("pc-test", info.PcId);
    }

    [Fact]
    public async Task Pair_then_snapshot_with_token_succeeds_and_without_token_is_401()
    {
        var (code, _) = _pairing.Begin();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new PairRequest(code, "Phone", null), MobileProtocol.Json));
        var (status, json) = await _api.HandleRestAsync(Req("POST", "/api/v1/pair"), body, CancellationToken.None);
        Assert.Equal(200, status);
        var paired = JsonSerializer.Deserialize<PairResponse>(json, MobileProtocol.Json)!;

        Assert.Equal(401, (await _api.HandleRestAsync(Req("GET", "/api/v1/snapshot"), null, CancellationToken.None)).Status);
        Assert.Equal(401, (await _api.HandleRestAsync(Req("GET", "/api/v1/snapshot", "nope"), null, CancellationToken.None)).Status);

        var (snapStatus, snapJson) = await _api.HandleRestAsync(Req("GET", "/api/v1/snapshot", paired.Token), null, CancellationToken.None);
        Assert.Equal(200, snapStatus);
        Assert.Contains("\"schema\":1", snapJson);
        Assert.False(_sampler.Started); // the one-shot snapshot left nothing running
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"code\":\"wrong\",\"deviceName\":\"x\"}")]
    public async Task Bad_pair_requests_are_400(string body)
    {
        _pairing.Begin();
        var (status, _) = await _api.HandleRestAsync(Req("POST", "/api/v1/pair"), Encoding.UTF8.GetBytes(body), CancellationToken.None);
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Unknown_routes_and_methods()
    {
        Assert.Equal(404, (await _api.HandleRestAsync(Req("GET", "/nope"), null, CancellationToken.None)).Status);
        Assert.Equal(405, (await _api.HandleRestAsync(Req("DELETE", "/api/v1/info"), null, CancellationToken.None)).Status);
    }

    [Fact]
    public void Hub_samples_only_while_watched_and_notifies_the_tray()
    {
        var events = new List<bool>();
        _hub.WatchingChanged += events.Add;

        var a = _hub.Subscribe();
        var b = _hub.Subscribe();
        Assert.True(_sampler.Started);
        a.Dispose();
        Assert.True(_sampler.Started);
        b.Dispose();
        b.Dispose(); // idempotent

        Assert.False(_sampler.Started);
        Assert.Equal(1, _sampler.StartCount);
        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void Fresh_session_report_produces_apps_and_missing_report_reports_no_session()
    {
        _sampler.Processes.Add(new ProcessSample(10, 1, "app", 1, ResourceUsage.Zero));
        Assert.False(_hub.Build().SessionAvailable);

        _hub.UpdateSession(new SessionReport(new[] { new SessionWindow(1, 10, "App", true) }, Array.Empty<ClaudeSession>()));
        var fresh = _hub.Build();
        Assert.True(fresh.SessionAvailable);
        Assert.Single(fresh.Apps);
    }
}
