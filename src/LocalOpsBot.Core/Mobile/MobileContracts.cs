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

/// <summary>Content of the pairing QR code (JSON text). <c>Hb</c> marks it as a Homebase payload.</summary>
public sealed record PairingOffer(
    int Hb,
    string PcId,
    string PcName,
    IReadOnlyList<string> Hosts,
    int Port,
    string Code,
    DateTimeOffset ExpiresAt);

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
