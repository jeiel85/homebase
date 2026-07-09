using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Advisor;

/// <summary>A single threshold breach — the kind that tripped and a human-readable detail.</summary>
public sealed record HealthBreach(string Kind, string Detail);

/// <summary>
/// Pure threshold evaluation over the current readings (no I/O), so it is easy to unit-test.
/// A reading counts as a breach when it meets or exceeds the configured limit.
/// </summary>
public static class HealthThresholdEvaluator
{
    public static IReadOnlyList<HealthBreach> Evaluate(
        SystemMetricSnapshot? metrics,
        IReadOnlyList<DiskSnapshot>? disks,
        TemperatureSnapshot? temperatures,
        AdvisorAlertOptions options)
    {
        var breaches = new List<HealthBreach>();

        if (metrics?.CpuUsagePercent is double cpu && cpu >= options.CpuPercent)
            breaches.Add(new HealthBreach("cpu", $"CPU {cpu:F0}% ≥ {options.CpuPercent:F0}%"));

        if (MemoryPercent(metrics) is double mem && mem >= options.MemoryPercent)
            breaches.Add(new HealthBreach("memory", $"RAM {mem:F0}% ≥ {options.MemoryPercent:F0}%"));

        if (disks is not null)
        {
            foreach (var d in disks)
                if (d.IsReady && d.UsedPercent >= options.DiskPercent)
                    breaches.Add(new HealthBreach(
                        $"disk:{d.Name}", $"Disk {d.Name} {d.UsedPercent:F0}% used ≥ {options.DiskPercent:F0}%"));
        }

        if (temperatures is not null)
        {
            foreach (var kind in new[] { "Cpu", "Gpu", "Board" })
            {
                var group = temperatures.Sensors.Where(s => s.Kind == kind).ToList();
                if (group.Count == 0) continue;
                var max = group.Max(s => s.Celsius);
                if (max >= options.TempCelsius)
                    breaches.Add(new HealthBreach(
                        $"temp:{kind}", $"{kind} temp {max:F0}°C ≥ {options.TempCelsius:F0}°C"));
            }
        }

        return breaches;
    }

    // Prefer the collector's computed percent; fall back to total/available when it is absent.
    private static double? MemoryPercent(SystemMetricSnapshot? m)
    {
        if (m is null) return null;
        if (m.MemoryUsagePercent is double pct) return pct;
        if (m is { TotalMemoryBytes: long total and > 0, AvailableMemoryBytes: long avail })
            return (double)(total - avail) / total * 100;
        return null;
    }
}
