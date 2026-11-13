namespace LocalOpsBot.Core.Mobile;

/// <summary>Config section <c>mobile</c>. Off by default — the Tray's "휴대폰 연결" turns it on.</summary>
public sealed class MobileOptions
{
    public const string SectionName = "mobile";

    public bool Enabled { get; set; }

    public int Port { get; set; } = MobileProtocol.DefaultPort;

    /// <summary>Listen on this PC's Tailscale address(es) (plain HTTP inside the tailnet).</summary>
    public bool Tailscale { get; set; } = true;

    /// <summary>
    /// Also listen on private LAN addresses (10/8, 172.16/12, 192.168/16) of adapters with a default
    /// gateway, over TLS with a pinned self-signed certificate. Firewall: LocalSubnet, Private profile.
    /// </summary>
    public bool Lan { get; set; }

    public CloudflareOptions Cloudflare { get; set; } = new();

    /// <summary>
    /// Also listen on 127.0.0.1 (development / emulator only). Enabled implicitly for the
    /// Cloudflare Tunnel, whose connector reaches the API on loopback.
    /// </summary>
    public bool BindLoopback { get; set; }

    /// <summary>Live push interval while a phone is watching. Clamped to 500..10000 ms.</summary>
    public int LiveIntervalMs { get; set; } = 1000;

    /// <summary>Let the Tray report active Claude Code session titles (title/project/time only).</summary>
    public bool ClaudeSessions { get; set; } = true;

    /// <summary>
    /// SID of the Windows user whose Tray may use the mobile bridge pipe (written by the Tray when it
    /// enables the feature). Empty → the INTERACTIVE group.
    /// </summary>
    public string? UserSid { get; set; }

    public int ClampedIntervalMs => Math.Clamp(LiveIntervalMs, 500, 10_000);
}

/// <summary>
/// Config section <c>mobile:cloudflare</c>. With a <see cref="Token"/> (and <see cref="Hostname"/>)
/// the Agent runs that named tunnel; without one it runs an account-less Quick Tunnel whose
/// trycloudflare.com address changes on every start (DECISIONS M-17).
/// </summary>
public sealed class CloudflareOptions
{
    public bool Enabled { get; set; }

    /// <summary>Named-tunnel token from the Cloudflare dashboard, or <c>ENV:VARNAME</c>. Empty → Quick Tunnel.</summary>
    public string? Token { get; set; }

    /// <summary>Public hostname routed to this tunnel (e.g. <c>home-pc.example.com</c>). Required with a token.</summary>
    public string? Hostname { get; set; }

    /// <summary>Full path to cloudflared.exe. Empty → the winget/MSI install locations, then PATH.</summary>
    public string? CloudflaredPath { get; set; }

    /// <summary>The token with <c>ENV:</c> indirection resolved; <c>null</c> when unset.</summary>
    public string? ResolveToken()
    {
        var raw = Token?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.StartsWith("ENV:", StringComparison.Ordinal))
        {
            var value = Environment.GetEnvironmentVariable(raw[4..])?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        return raw;
    }
}
