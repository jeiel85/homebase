using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// The two places the Agent uses the local AI on its own, as Settings › AI advice shows them
/// (DECISIONS M-61): <see cref="AutoAdvice"/> — <c>advisorAlerts.enabled</c>, advice pushed when the PC
/// stays over a threshold — and <see cref="InterpretErrors"/> — <c>eventLog.llmInterpret</c>, a note
/// added to event-log error alerts. The rest is shown, not changed here.
/// </summary>
internal sealed record AiAutomationState(
    bool AutoAdvice,
    bool InterpretErrors,
    bool EventLogOn,
    double CpuPercent,
    double MemoryPercent,
    double DiskPercent,
    double TempCelsius,
    int IntervalSeconds,
    int ConsecutiveBreaches,
    int CooldownMinutes)
{
    /// <summary>How long a breach must last before advice is sent: the polls in a row × the poll interval.</summary>
    public int SustainMinutes => Math.Max(1, (int)Math.Ceiling(Math.Max(1, ConsecutiveBreaches) * Math.Max(30, IntervalSeconds) / 60.0));
}

/// <summary>Reads and writes <see cref="AiAutomationState"/> in the Agent config.</summary>
internal static class AiAutomationConfig
{
    /// <summary>The effective state, with the Agent's defaults (both off) where the config is silent.</summary>
    public static AiAutomationState Read(IConfiguration config) => new(
        Bool(config["advisorAlerts:enabled"], false),
        Bool(config["eventLog:llmInterpret"], false),
        Bool(config["eventLog:enabled"], true),
        Number(config["advisorAlerts:cpuPercent"], 90),
        Number(config["advisorAlerts:memoryPercent"], 90),
        Number(config["advisorAlerts:diskPercent"], 95),
        Number(config["advisorAlerts:tempCelsius"], 85),
        (int)Number(config["advisorAlerts:intervalSeconds"], 300),
        (int)Number(config["advisorAlerts:consecutiveBreaches"], 2),
        (int)Number(config["advisorAlerts:cooldownMinutes"], 60));

    /// <summary>
    /// Writes both switches with one administrator approval (the Agent restarts and reads them). Returns
    /// <c>false</c> when they were not saved (approval declined, or the elevated step failed).
    /// </summary>
    public static async Task<bool> SaveAsync(bool autoAdvice, bool interpretErrors)
    {
        var root = await ElevatedConfig.ReadAsync();
        Apply(root, autoAdvice, interpretErrors);
        return await ElevatedConfig.WriteAsync(root);
    }

    internal static void Apply(JsonObject root, bool autoAdvice, bool interpretErrors)
    {
        ElevatedConfig.SetValue(root, "advisorAlerts", "enabled", autoAdvice);
        ElevatedConfig.SetValue(root, "eventLog", "llmInterpret", interpretErrors);
    }

    private static bool Bool(string? value, bool fallback) => bool.TryParse(value, out var b) ? b : fallback;

    private static double Number(string? value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
