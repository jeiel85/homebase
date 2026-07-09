namespace LocalOpsBot.Core.Advisor;

/// <summary>
/// Configuration for active health advice: a background monitor that watches the machine and,
/// when a threshold is breached, asks the local LLM for advice and sends it to Telegram.
/// Bound from the "advisorAlerts" config section. Opt-in (disabled by default) because it sends
/// LLM output automatically and needs a running Ollama server.
/// </summary>
public sealed class AdvisorAlertOptions
{
    /// <summary>Master switch. When false, the background monitor does nothing.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>How often to sample the machine and evaluate thresholds.</summary>
    public int IntervalSeconds { get; init; } = 300;

    /// <summary>
    /// Minimum time between two auto-advisories, so an ongoing problem is not re-sent every poll
    /// (and the LLM is not called repeatedly). Should stay well above the alert dedup window.
    /// </summary>
    public int CooldownMinutes { get; init; } = 60;

    /// <summary>
    /// Number of consecutive breached polls required before advising, so a brief spike does not
    /// trigger advice. 1 means advise on the first breached poll.
    /// </summary>
    public int ConsecutiveBreaches { get; init; } = 2;

    /// <summary>CPU load percent that counts as a breach (inclusive).</summary>
    public double CpuPercent { get; init; } = 90;

    /// <summary>Memory usage percent that counts as a breach (inclusive).</summary>
    public double MemoryPercent { get; init; } = 90;

    /// <summary>Disk used percent that counts as a breach, per drive (inclusive).</summary>
    public double DiskPercent { get; init; } = 95;

    /// <summary>Temperature in °C that counts as a breach, per category (inclusive).</summary>
    public double TempCelsius { get; init; } = 85;

    /// <summary>
    /// Returns a copy with every value clamped to a sane range, so a config typo (e.g. a 0%
    /// threshold or a 1-second interval) can't turn into spammy or nonsensical behaviour.
    /// </summary>
    public AdvisorAlertOptions Normalized() => new()
    {
        Enabled = Enabled,
        IntervalSeconds = Math.Clamp(IntervalSeconds, 30, 86_400),
        CooldownMinutes = Math.Clamp(CooldownMinutes, 1, 1_440),
        ConsecutiveBreaches = Math.Clamp(ConsecutiveBreaches, 1, 100),
        CpuPercent = Math.Clamp(CpuPercent, 1, 100),
        MemoryPercent = Math.Clamp(MemoryPercent, 1, 100),
        DiskPercent = Math.Clamp(DiskPercent, 1, 100),
        TempCelsius = Math.Clamp(TempCelsius, 20, 150),
    };
}
