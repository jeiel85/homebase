using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Monitoring;
using Xunit;

namespace LocalOpsBot.Tests.Core.Advisor;

public sealed class HealthThresholdEvaluatorTests
{
    private static readonly AdvisorAlertOptions Opts = new(); // cpu90 mem90 disk95 temp85

    private static SystemMetricSnapshot Metrics(double? cpu = 0, double? memPct = 0) =>
        new(DateTimeOffset.UtcNow, cpu, 16_000_000_000, 8_000_000_000, memPct, TimeSpan.Zero, "PC", null);

    [Fact]
    public void All_below_thresholds_no_breach()
    {
        var breaches = HealthThresholdEvaluator.Evaluate(Metrics(cpu: 50, memPct: 50), null, null, Opts);
        Assert.Empty(breaches);
    }

    [Fact]
    public void Null_snapshots_no_breach()
    {
        Assert.Empty(HealthThresholdEvaluator.Evaluate(null, null, null, Opts));
    }

    [Theory]
    [InlineData(89.9, false)]
    [InlineData(90.0, true)] // inclusive boundary
    [InlineData(97.0, true)]
    public void Cpu_breach_is_inclusive(double cpu, bool expected)
    {
        var breaches = HealthThresholdEvaluator.Evaluate(Metrics(cpu: cpu), null, null, Opts);
        Assert.Equal(expected, breaches.Any(b => b.Kind == "cpu"));
    }

    [Fact]
    public void Memory_uses_computed_percent_when_percent_absent()
    {
        // MemoryUsagePercent null, total 100 / available 5 => 95% used >= 90
        var m = new SystemMetricSnapshot(DateTimeOffset.UtcNow, 0, 100, 5, null, TimeSpan.Zero, "PC", null);
        Assert.Contains(HealthThresholdEvaluator.Evaluate(m, null, null, Opts), b => b.Kind == "memory");
    }

    [Fact]
    public void Disk_breach_per_ready_drive_only()
    {
        var disks = new List<DiskSnapshot>
        {
            new("C:\\", "Fixed", 100, 2, 98, 98.0, true),    // breach
            new("D:\\", "Fixed", 100, 50, 50, 50.0, true),   // ok
            new("E:\\", "Fixed", 100, 0, 100, 100.0, false), // not ready -> skip
        };
        var breaches = HealthThresholdEvaluator.Evaluate(Metrics(), disks, null, Opts);
        Assert.Contains(breaches, b => b.Kind == "disk:C:\\");
        Assert.DoesNotContain(breaches, b => b.Kind == "disk:D:\\");
        Assert.DoesNotContain(breaches, b => b.Kind == "disk:E:\\");
    }

    [Fact]
    public void Temp_breach_uses_per_kind_max()
    {
        var temps = new TemperatureSnapshot(new List<SensorReading>
        {
            new("GPU Core", "Gpu", 80),
            new("GPU Hot Spot", "Gpu", 88), // max 88 >= 85 -> breach
            new("CPU Package", "Cpu", 70),  // below
        });
        var breaches = HealthThresholdEvaluator.Evaluate(Metrics(), null, temps, Opts);
        Assert.Contains(breaches, b => b.Kind == "temp:Gpu");
        Assert.DoesNotContain(breaches, b => b.Kind == "temp:Cpu");
    }
}
