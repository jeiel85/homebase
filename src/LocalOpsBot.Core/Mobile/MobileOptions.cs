namespace LocalOpsBot.Core.Mobile;

/// <summary>Config section <c>mobile</c>. Off by default — the Tray's "휴대폰 연결" turns it on.</summary>
public sealed class MobileOptions
{
    public const string SectionName = "mobile";

    public bool Enabled { get; set; }

    public int Port { get; set; } = MobileProtocol.DefaultPort;

    /// <summary>Also listen on 127.0.0.1 (development / emulator only). Never on LAN addresses.</summary>
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
