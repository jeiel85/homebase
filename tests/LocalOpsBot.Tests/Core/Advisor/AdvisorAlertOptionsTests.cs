using LocalOpsBot.Core.Advisor;
using Xunit;

namespace LocalOpsBot.Tests.Core.Advisor;

public sealed class AdvisorAlertOptionsTests
{
    [Fact]
    public void Normalized_clamps_out_of_range_values()
    {
        var raw = new AdvisorAlertOptions
        {
            IntervalSeconds = 1,
            CooldownMinutes = 0,
            ConsecutiveBreaches = 0,
            CpuPercent = 0,
            MemoryPercent = 250,
            DiskPercent = -5,
            TempCelsius = 5,
        };

        var n = raw.Normalized();

        Assert.Equal(30, n.IntervalSeconds);
        Assert.Equal(1, n.CooldownMinutes);
        Assert.Equal(1, n.ConsecutiveBreaches);
        Assert.Equal(1, n.CpuPercent);
        Assert.Equal(100, n.MemoryPercent);
        Assert.Equal(1, n.DiskPercent);
        Assert.Equal(20, n.TempCelsius);
    }

    [Fact]
    public void Normalized_preserves_in_range_values()
    {
        var raw = new AdvisorAlertOptions
        {
            Enabled = true,
            IntervalSeconds = 300,
            CooldownMinutes = 60,
            ConsecutiveBreaches = 2,
            CpuPercent = 90,
            TempCelsius = 85,
        };

        var n = raw.Normalized();

        Assert.True(n.Enabled);
        Assert.Equal(300, n.IntervalSeconds);
        Assert.Equal(60, n.CooldownMinutes);
        Assert.Equal(2, n.ConsecutiveBreaches);
        Assert.Equal(90, n.CpuPercent);
        Assert.Equal(85, n.TempCelsius);
    }
}
