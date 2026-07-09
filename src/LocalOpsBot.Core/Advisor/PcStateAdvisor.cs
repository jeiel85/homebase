using System.Globalization;
using System.Text;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Advisor;

/// <summary>Outcome of an advisory request: the LLM's guidance, or a reason it couldn't be produced.</summary>
public sealed record AdvisoryResult(bool Ok, string Text, string? Error);

public interface IPcStateAdvisor
{
    /// <summary>Collects the current PC state and asks the local LLM for health guidance.</summary>
    Task<AdvisoryResult> AdviseAsync(CancellationToken ct);

    /// <summary>The compact, human-readable state summary fed to the model (also handy for tests).</summary>
    Task<string> BuildStateSummaryAsync(CancellationToken ct);
}

/// <summary>
/// Turns the machine's current readings (CPU/RAM/disk/network/uptime + recent alerts) into a
/// short summary, prompts the local LLM for plain-language health advice, and returns it.
/// </summary>
public sealed class PcStateAdvisor : IPcStateAdvisor
{
    private const double GiB = 1024.0 * 1024 * 1024;

    private readonly LlmAdvisorOptions _options;
    private readonly ILlmClient _llm;
    private readonly ISystemMetricsCollector _metrics;
    private readonly IDiskCollector _disk;
    private readonly INetworkStatusChecker _network;
    private readonly IAlertStore _alerts;
    private readonly ITemperatureCollector _temperature;

    public PcStateAdvisor(
        LlmAdvisorOptions options,
        ILlmClient llm,
        ISystemMetricsCollector metrics,
        IDiskCollector disk,
        INetworkStatusChecker network,
        IAlertStore alerts,
        ITemperatureCollector temperature)
    {
        _options = options;
        _llm = llm;
        _metrics = metrics;
        _disk = disk;
        _network = network;
        _alerts = alerts;
        _temperature = temperature;
    }

    public async Task<AdvisoryResult> AdviseAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
            return new AdvisoryResult(false, "", "The AI advisor is turned off (set llmAdvisor.enabled to true).");

        var summary = await BuildStateSummaryAsync(ct);
        var result = await _llm.GenerateAsync(BuildPrompt(summary), ct);
        return result.Ok
            ? new AdvisoryResult(true, result.Text, null)
            : new AdvisoryResult(false, "", result.Error);
    }

    public async Task<string> BuildStateSummaryAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();

        var m = await _metrics.CollectAsync(ct);
        if (m.Success && m.Snapshot is { } s)
        {
            sb.AppendLine($"Host: {s.HostName}{(string.IsNullOrEmpty(s.OsVersion) ? "" : $" ({s.OsVersion})")}");
            var u = s.Uptime;
            sb.AppendLine($"Uptime: {(u.Days > 0 ? $"{u.Days}d " : "")}{u.Hours}h {u.Minutes}m");
            if (s.CpuUsagePercent is double cpu)
                sb.AppendLine($"CPU load: {cpu:F0}%");
            if (s.TotalMemoryBytes is long total && s.AvailableMemoryBytes is long avail && total > 0)
            {
                var usedGb = (total - avail) / GiB;
                var totalGb = total / GiB;
                var pct = s.MemoryUsagePercent ?? (usedGb / totalGb * 100);
                sb.AppendLine($"RAM: {usedGb:F1}/{totalGb:F1} GB ({pct:F0}%)");
            }
        }

        var net = await _network.CollectAsync(ct);
        if (net.Success && net.Snapshot is { } n)
            sb.AppendLine($"Network: {(n.IsOnline ? "online" : "OFFLINE")}{(n.PingLatencyMs is long p ? $", ping {p}ms" : "")}");

        var disk = await _disk.CollectAsync(ct);
        if (disk.Success && disk.Snapshot is { } disks)
        {
            foreach (var d in disks)
            {
                if (!d.IsReady) continue;
                sb.AppendLine($"Disk {d.Name}: {d.FreeBytes / GiB:F0} GB free / {d.TotalBytes / GiB:F0} GB ({d.UsedPercent:F0}% used)");
            }
        }

        var temp = await _temperature.CollectAsync(ct);
        if (temp.Success && temp.Snapshot is { Sensors.Count: > 0 } t)
        {
            // Report the hottest sensor per category — that's what flags an abnormal reading.
            foreach (var (kind, label) in new[] { ("Cpu", "CPU"), ("Gpu", "GPU"), ("Board", "Board") })
            {
                var group = t.Sensors.Where(x => x.Kind == kind).ToList();
                if (group.Count == 0) continue;
                sb.AppendLine($"{label} temp: {group.Max(x => x.Celsius):F0}°C");
            }
        }

        try
        {
            var alerts = await _alerts.GetRecentAsync(20, ct);
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var recent = alerts.Where(a => a.CreatedAt >= cutoff).ToList();
            sb.AppendLine($"Alerts (last 24h): {recent.Count}");
            foreach (var a in recent.Take(3))
                sb.AppendLine($"  - [{a.Severity}] {a.Title}");
        }
        catch
        {
            // Alert history is best-effort context; omit it if the store is unavailable.
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildPrompt(string summary) =>
        "You are a concise Windows PC health advisor. Based ONLY on the current readings below, " +
        "point out anything abnormal or worth checking and suggest concrete next steps. " +
        "Use a few short bullet points. If everything looks healthy, say so in one line. " +
        "Do not invent readings that are not given. " +
        $"Write the reply in {ResolveLanguage()}.\n\n" +
        "Current readings:\n" + summary;

    // Reply language: the configured value, else the OS display language, else English.
    private string ResolveLanguage()
    {
        if (!string.IsNullOrWhiteSpace(_options.Language))
            return _options.Language.Trim();
        try
        {
            var ui = CultureInfo.InstalledUICulture;
            var neutral = ui.IsNeutralCulture ? ui : ui.Parent;
            var name = neutral.EnglishName;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("Invariant", StringComparison.Ordinal)
                ? "English" : name;
        }
        catch { return "English"; }
    }
}
