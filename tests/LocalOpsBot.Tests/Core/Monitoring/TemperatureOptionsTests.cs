using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LocalOpsBot.Tests.Core.Monitoring;

public class TemperatureOptionsTests
{
    [Fact]
    public void Default_is_enabled_Wmi()
    {
        var opts = new TemperatureOptions();
        Assert.True(opts.Enabled);
        // The security fix hinges on this: a fresh install must not load the WinRing0 driver.
        Assert.Equal(TemperatureSource.Wmi, opts.Source);
    }

    [Fact]
    public void Missing_source_key_binds_to_Wmi()
    {
        // Mirrors an existing install whose appsettings.json predates the "source" key.
        var opts = Bind(new Dictionary<string, string?> { ["temperature:enabled"] = "true" });
        Assert.True(opts.Enabled);
        Assert.Equal(TemperatureSource.Wmi, opts.Source);
    }

    [Theory]
    [InlineData("LibreHardware", TemperatureSource.LibreHardware)]
    [InlineData("librehardware", TemperatureSource.LibreHardware)]
    [InlineData("Wmi", TemperatureSource.Wmi)]
    [InlineData("wmi", TemperatureSource.Wmi)]
    public void Source_binds_case_insensitively(string value, TemperatureSource expected)
    {
        var opts = Bind(new Dictionary<string, string?>
        {
            ["temperature:enabled"] = "true",
            ["temperature:source"] = value,
        });
        Assert.Equal(expected, opts.Source);
    }

    private static TemperatureOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return config.GetSection("temperature").Get<TemperatureOptions>() ?? new TemperatureOptions();
    }
}
