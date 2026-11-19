using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>The paths kept for a linked PC (DECISIONS M-29, M-59).</summary>
public class HubPathsTests
{
    private const string Pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly MobileEndpoint Lan = new(MobileTransport.Lan, "192.168.45.101", 47820, true, Pin);
    private static readonly MobileEndpoint TsName = new(MobileTransport.Tailscale, "loq-win11.tail1234.ts.net", 47820, false);
    private static readonly MobileEndpoint TsIp = new(MobileTransport.Tailscale, "100.123.134.41", 47820, false);
    private static readonly MobileEndpoint OldTunnel = new(MobileTransport.Cloudflare, "old-name.trycloudflare.com", 443, true);
    private static readonly MobileEndpoint NewTunnel = new(MobileTransport.Cloudflare, "new-name.trycloudflare.com", 443, true);

    // What happened between two PCs: the 10-minute read came while the other PC's Tailscale was down, and its
    // Tailscale paths were gone from this PC's copy — then its Quick Tunnel restarted, and nothing was left.
    [Fact]
    public void A_list_read_while_the_pcs_tailscale_was_down_keeps_its_tailscale_paths()
    {
        var known = new[] { Lan, TsName, TsIp, OldTunnel };

        var kept = HubPaths.Refresh(new[] { Lan, NewTunnel }, known);

        Assert.Equal(new[] { Lan, TsName, TsIp, NewTunnel }, kept);
    }

    [Fact]
    public void A_list_with_tailscale_paths_is_taken_as_it_is()
    {
        var other = new MobileEndpoint(MobileTransport.Tailscale, "100.64.0.9", 47820, false);
        Assert.Equal(new[] { other, NewTunnel }, HubPaths.Refresh(new[] { other, NewTunnel }, new[] { TsIp, OldTunnel }));
        // Nothing to keep: the fresh list as it is.
        Assert.Equal(new[] { Lan }, HubPaths.Refresh(new[] { Lan }, new[] { Lan, OldTunnel }));
    }

    [Fact]
    public void Only_paths_a_pc_can_really_have_are_taken_from_its_presence()
    {
        var advertised = new[]
        {
            Lan, TsName, TsIp, NewTunnel,
            Lan with { CertSha256 = null },                                       // LAN without its pin
            Lan with { Host = "8.8.8.8" },                                        // LAN that isn't private
            Lan with { Tls = false },                                             // LAN in the clear
            TsIp with { Host = "203.0.113.7" },                                   // "Tailscale" on the internet, plain HTTP
            TsName with { Host = "evil.example.com" },                            // "Tailscale" name off the tailnet
            NewTunnel with { Tls = false },                                       // tunnel in the clear
            NewTunnel with { Host = "203.0.113.7" },                              // tunnel to a bare address
            new MobileEndpoint(MobileTransport.Loopback, "127.0.0.1", 47820, false), // this PC's own Agent
            new MobileEndpoint("carrier-pigeon", "coop", 1, false),
            TsIp with { Port = 0 },
            TsIp,                                                                 // a repeat
        };

        Assert.Equal(new[] { Lan, TsName, TsIp, NewTunnel }, HubPaths.Usable(advertised));
        Assert.Empty(HubPaths.Usable(null));
    }

    [Fact]
    public void At_most_eight_paths_are_taken()
    {
        var many = Enumerable.Range(1, 12).Select(i => new MobileEndpoint(MobileTransport.Tailscale, $"100.100.0.{i}", 47820, false)).ToList();
        Assert.Equal(HubPaths.MaxAdvertised, HubPaths.Usable(many).Count);
    }

    [Fact]
    public void An_entry_with_missing_fields_from_the_wire_is_skipped()
    {
        var report = JsonSerializer.Deserialize<PresenceReport>(
            """{"schema":1,"kind":"pc","pcId":"pc-b","endpoints":[{"transport":"tailscale","host":"100.123.134.41","port":47820,"tls":false},{"transport":null,"host":null}]}""",
            MobileProtocol.Json)!;
        Assert.Equal(new[] { TsIp }, HubPaths.Usable(report.Sanitized().Endpoints));
    }

    [Theory]
    [InlineData(530, true)]   // Cloudflare 1033: the tunnel behind the name is gone
    [InlineData(502, true)]
    [InlineData(504, true)]
    [InlineData(522, true)]
    [InlineData(503, false)]  // the Agent's own "busy" / "forwarding off"
    [InlineData(401, false)]
    [InlineData(200, false)]
    public void Cloudflare_errors_for_a_gone_tunnel_try_the_next_path(int status, bool next) =>
        Assert.Equal(next, HubClient.IsTunnelDown(status));
}

public class PresencePathsTests
{
    private static readonly MobileEndpoint Ts = new(MobileTransport.Tailscale, "100.123.134.41", 47820, false);

    [Fact]
    public void A_pc_names_its_paths_and_a_phone_does_not()
    {
        Assert.Equal(new[] { Ts }, new PresenceReport(1, DeviceKind.Pc, Endpoints: new[] { Ts }).Sanitized().Endpoints);
        Assert.Null(new PresenceReport(1, DeviceKind.Phone, Endpoints: new[] { Ts }).Sanitized().Endpoints);
        var tooMany = Enumerable.Repeat(Ts, PresenceReport.MaxEndpoints + 1).ToList();
        Assert.Null(new PresenceReport(1, DeviceKind.Pc, Endpoints: tooMany).Sanitized().Endpoints);
        // Not a reason to refuse the report (M-46): the rest of it still counts.
        Assert.Null(PresenceReport.Validate(new PresenceReport(1, DeviceKind.Pc, CpuPct: 12, Endpoints: tooMany)));
    }

    [Fact]
    public void An_older_hub_reads_a_presence_with_paths_as_before()
    {
        var json = JsonSerializer.Serialize(new PresenceReport(1, DeviceKind.Pc, CpuPct: 12, PcId: "pc-b", Endpoints: new[] { Ts }), MobileProtocol.Json);
        Assert.Contains("\"endpoints\":[", json);
        // Without the field, as an Agent before v0.45.0 sends it, nothing changes.
        var old = JsonSerializer.Serialize(new PresenceReport(1, DeviceKind.Pc, CpuPct: 12, PcId: "pc-b"), MobileProtocol.Json);
        Assert.DoesNotContain("endpoints", old);
    }
}
