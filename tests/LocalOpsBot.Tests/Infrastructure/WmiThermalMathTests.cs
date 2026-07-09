using LocalOpsBot.Infrastructure.Windows;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure;

public class WmiThermalMathTests
{
    [Theory]
    [InlineData(3000, 26.85)]   // 300.0 K
    [InlineData(3132, 40.05)]   // 313.2 K
    [InlineData(3931, 119.95)]  // 393.1 K, near the upper plausible bound
    public void TryToCelsius_converts_plausible_readings(double tenthsKelvin, double expected)
    {
        Assert.True(WmiThermalMath.TryToCelsius(tenthsKelvin, out var celsius));
        Assert.Equal(expected, celsius, 2);
    }

    [Theory]
    [InlineData(0)]      // -273.15 C: driver not ready
    [InlineData(2731)]   // -0.05 C: just below zero
    [InlineData(5000)]   // 226.85 C: above any real component temperature
    public void TryToCelsius_rejects_implausible_readings(double tenthsKelvin)
    {
        Assert.False(WmiThermalMath.TryToCelsius(tenthsKelvin, out _));
    }

    [Theory]
    [InlineData(@"ACPI\ThermalZone\THM0_0", "THM0_0")]
    [InlineData(@"ACPI\ThermalZone\TZ._0", "TZ._0")]
    [InlineData("NOSLASH", "NOSLASH")]
    [InlineData(null, "ACPI Thermal Zone")]
    [InlineData("", "ACPI Thermal Zone")]
    public void CleanZoneName_returns_last_segment(string? instanceName, string expected)
    {
        Assert.Equal(expected, WmiThermalMath.CleanZoneName(instanceName));
    }
}
