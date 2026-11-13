using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Wire contract between the Agent's mobile API and the Homebase Android app
/// (<c>docs/MOBILE_PROTOCOL.md</c>). Every top-level message carries <see cref="MobileProtocol.Schema"/>.
/// Field names are camelCase on the wire; the Android parser mirrors these records.
/// </summary>
public static class MobileProtocol
{
    /// <summary>Snapshot/message shape version. Bump on any breaking field change.</summary>
    public const int Schema = 1;

    /// <summary>API surface version advertised by <c>/api/v1/info</c>.</summary>
    public const int Protocol = 1;

    public const int DefaultPort = 47820;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Unauthenticated identity probe — enough for the PC list to show "online" and the version.</summary>
public sealed record PcInfo(
    int Schema,
    int Protocol,
    string PcId,
    string PcName,
    string AgentVersion,
    string Os);

/// <summary>Network path names used in <see cref="MobileEndpoint.Transport"/> (DECISIONS M-15~M-18).</summary>
public static class MobileTransport
{
    /// <summary>Tailnet address or MagicDNS name; plain HTTP (WireGuard encrypts the hop).</summary>
    public const string Tailscale = "tailscale";

    /// <summary>Private LAN address; TLS with a self-signed certificate the app pins by SHA-256.</summary>
    public const string Lan = "lan";

    /// <summary>Cloudflare Tunnel hostname; public TLS at Cloudflare's edge, port 443.</summary>
    public const string Cloudflare = "cloudflare";

    /// <summary>127.0.0.1 with <c>mobile:bindLoopback</c> — development/emulator (adb reverse) only.</summary>
    public const string Loopback = "loopback";
}

/// <summary>
/// One way to reach this PC. <c>CertSha256</c> (lowercase hex of the DER certificate) is set for
/// <see cref="MobileTransport.Lan"/>: the app accepts that certificate and nothing else.
/// </summary>
public sealed record MobileEndpoint(string Transport, string Host, int Port, bool Tls, string? CertSha256 = null);

/// <summary>
/// Content of the pairing QR code (JSON text). <c>Hb</c> marks it as a Homebase payload.
/// <c>Hosts</c> + <c>Port</c> are the v1 Tailscale-only fields older apps read; <c>Endpoints</c>
/// lists every path (Tailscale, LAN, Cloudflare) and is what current apps use.
/// </summary>
public sealed record PairingOffer(
    int Hb,
    string PcId,
    string PcName,
    IReadOnlyList<string> Hosts,
    int Port,
    string Code,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<MobileEndpoint> Endpoints);

/// <summary><c>GET /api/v1/endpoints</c>: the current paths, so a paired phone follows DHCP or tunnel-address changes.</summary>
public sealed record EndpointsResponse(int Schema, IReadOnlyList<MobileEndpoint> Endpoints);

public sealed record PairRequest(string Code, string DeviceName, string? DeviceModel);

public sealed record PairResponse(int Schema, string DeviceId, string Token, string PcId, string PcName);

public sealed record ErrorResponse(int Schema, string Error, string Message);

public sealed record SystemTotals(
    double CpuPct,
    long MemUsedBytes,
    long MemTotalBytes,
    long IoBps,
    long NetRxBps,
    long NetTxBps,
    long UptimeSec);

/// <summary>Per-process (or aggregated) resource rates for one sampling interval.</summary>
public sealed record ResourceUsage(double CpuPct, long MemBytes, long IoBps, long? NetRxBps, long? NetTxBps)
{
    public static readonly ResourceUsage Zero = new(0, 0, 0, null, null);
}

public sealed record ProcessEntry(int Pid, int ParentPid, string Name, ResourceUsage Usage);

public sealed record WindowTitle(string Title, bool Foreground);

/// <summary>
/// One app as the user sees it: the process owning one or more top-level windows, with its
/// usage aggregated over itself and its window-less descendants (e.g. Windows Terminal → pwsh → node).
/// </summary>
public sealed record AppEntry(
    int Pid,
    string Process,
    bool Foreground,
    IReadOnlyList<WindowTitle> Windows,
    ResourceUsage Usage,
    IReadOnlyList<ProcessEntry> Children);

public sealed record ClaudeSession(string Title, string? Project, DateTimeOffset LastActivity);

public sealed record LiveSnapshot(
    int Schema,
    DateTimeOffset Ts,
    string PcName,
    SystemTotals System,
    bool SessionAvailable,
    bool NetPerProcess,
    IReadOnlyList<AppEntry> Apps,
    IReadOnlyList<ProcessEntry> Processes,
    IReadOnlyList<ClaudeSession> ClaudeSessions);
