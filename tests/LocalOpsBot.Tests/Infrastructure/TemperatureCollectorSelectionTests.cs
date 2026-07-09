using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure;
using LocalOpsBot.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure;

// Verifies the real DI registration in AddLocalOpsWindowsCollectors picks the temperature backend
// from config. Resolving only constructs the collector (no driver load / WMI query), so it is safe
// and fast. Windows-only, matching the product.
[SupportedOSPlatform("windows")]
public class TemperatureCollectorSelectionTests
{
    [Fact]
    public void Default_options_select_the_WMI_collector()
    {
        Assert.IsType<WmiTemperatureCollector>(Resolve(new TemperatureOptions()));
    }

    [Fact]
    public void LibreHardware_source_selects_the_LHM_collector()
    {
        var options = new TemperatureOptions { Source = TemperatureSource.LibreHardware };
        Assert.IsType<LibreHardwareTemperatureCollector>(Resolve(options));
    }

    private static ITemperatureCollector Resolve(TemperatureOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddLocalOpsWindowsCollectors();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ITemperatureCollector>();
    }
}
