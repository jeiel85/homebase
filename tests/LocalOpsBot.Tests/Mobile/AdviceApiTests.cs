using System.Text;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using LocalOpsBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>
/// <c>POST /api/v1/advise</c> (DECISIONS M-59): a paired device asks for the local LLM's advice about
/// this PC; a failure to produce it is still a handled request with <c>ok: false</c> and a code.
/// </summary>
public sealed class AdviceApiTests
{
    private readonly InMemoryDeviceStore _store = new();
    private readonly PairingService _pairing;
    private readonly FakePcStateAdvisor _advisor = new();

    public AdviceApiTests() => _pairing = new PairingService(_store, new ManualTimeProvider());

    private MobileApiServer Api(bool withAdvice = true)
    {
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        return new MobileApiServer(hub, _pairing, _store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options,
            NullLogger<MobileApiServer>.Instance,
            advice: withAdvice ? new OnDemandAdvice(_advisor, new LlmAdvisorOptions { Model = "llama3.2:1b" }, new ManualTimeProvider()) : null);
    }

    private string Pair(string kind = DeviceKind.Phone)
    {
        var (code, _) = _pairing.Begin();
        return _pairing.TryPair(code, kind == DeviceKind.Pc ? "OTHER-PC" : "S24", null, kind).Token!;
    }

    private static HttpRequestHead Req(string method, string path, string? token = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return new HttpRequestHead(method, path, headers);
    }

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"schema\":1}");

    [Fact]
    public async Task A_paired_phone_gets_the_advice_and_what_it_was_based_on()
    {
        _advisor.NextResult = new AdvisoryResult(true, "- CPU is busy: close Chrome.", null, null, "CPU load: 92%");
        var (status, json) = await Api().HandleRestAsync(Req("POST", "/api/v1/advise", Pair()), Body, CancellationToken.None);

        Assert.Equal(200, status);
        var r = JsonSerializer.Deserialize<AdviceResponse>(json, MobileProtocol.Json)!;
        Assert.True(r.Ok);
        Assert.Equal("- CPU is busy: close Chrome.", r.Text);
        Assert.Equal("CPU load: 92%", r.Summary);
        Assert.Equal("llama3.2:1b", r.Model);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task A_missing_model_is_a_handled_reply_with_its_code()
    {
        _advisor.NextResult = new AdvisoryResult(false, "", "The model 'llama3.2:1b' may not be installed", AdviceErrors.ModelMissing);
        var (status, json) = await Api().HandleRestAsync(Req("POST", "/api/v1/advise", Pair()), Body, CancellationToken.None);

        Assert.Equal(200, status);
        var r = JsonSerializer.Deserialize<AdviceResponse>(json, MobileProtocol.Json)!;
        Assert.False(r.Ok);
        Assert.Null(r.Text);
        Assert.Equal("model_missing", r.Error);
        Assert.Contains("may not be installed", r.Message);
    }

    [Fact]
    public async Task A_linked_pc_may_ask_too()
    {
        var (status, _) = await Api().HandleRestAsync(Req("POST", "/api/v1/advise", Pair(DeviceKind.Pc)), Body, CancellationToken.None);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Only_a_paired_device_may_ask()
    {
        var api = Api();
        Assert.Equal(401, (await api.HandleRestAsync(Req("POST", "/api/v1/advise"), Body, CancellationToken.None)).Status);
        Assert.Equal(401, (await api.HandleRestAsync(Req("POST", "/api/v1/advise", "nope"), Body, CancellationToken.None)).Status);
        Assert.Equal(0, _advisor.AdviseCallCount);
    }

    [Fact]
    public async Task Other_methods_are_refused_and_an_agent_without_advice_has_no_route()
    {
        var token = Pair();
        Assert.Equal(405, (await Api().HandleRestAsync(Req("GET", "/api/v1/advise", token), null, CancellationToken.None)).Status);
        Assert.Equal(404, (await Api(withAdvice: false).HandleRestAsync(Req("POST", "/api/v1/advise", token), Body, CancellationToken.None)).Status);
    }
}
