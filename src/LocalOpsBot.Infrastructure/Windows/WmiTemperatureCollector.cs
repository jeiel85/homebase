using System.Management;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Infrastructure.Windows;

/// <summary>
/// Reads temperature from WMI ACPI thermal zones (MSAcpi_ThermalZoneTemperature). Unlike
/// <see cref="LibreHardwareTemperatureCollector"/> this loads no kernel driver, so antivirus has
/// nothing to flag — but many desktops don't implement the class, so collection often returns an
/// empty list. This is the default temperature backend (see <see cref="TemperatureSource"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiTemperatureCollector : ITemperatureCollector
{
    // MSAcpi_ThermalZoneTemperature lives in root\WMI, not the default root\cimv2 scope.
    private const string Scope = @"root\WMI";
    private const string Query = "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature";

    private readonly TemperatureOptions _options;
    private readonly ILogger<WmiTemperatureCollector>? _logger;
    private readonly object _gate = new();
    private bool _loggedFailure; // guarded by _gate: warn on the first failure, then stay quiet until recovery

    // Options/logger are optional so the collector can be constructed directly (tests, smoke);
    // dependency injection supplies the bound options and a real logger in the running Agent.
    public WmiTemperatureCollector(
        TemperatureOptions? options = null,
        ILogger<WmiTemperatureCollector>? logger = null)
    {
        _options = options ?? new TemperatureOptions();
        _logger = logger;
    }

    public string Name => "Temperature";

    public Task<CollectorResult<TemperatureSnapshot>> CollectAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            // Master switch off: return empty without touching WMI.
            if (!_options.Enabled)
                return Task.FromResult(
                    CollectorResult<TemperatureSnapshot>.Ok(new TemperatureSnapshot([]), now));

            try
            {
                var readings = new List<SensorReading>();
                using var searcher = new ManagementObjectSearcher(Scope, Query);
                foreach (var mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["CurrentTemperature"] is not { } raw)
                            continue;
                        if (!WmiThermalMath.TryToCelsius(Convert.ToDouble(raw), out var celsius))
                            continue;
                        var name = WmiThermalMath.CleanZoneName(mo["InstanceName"] as string);
                        // ACPI thermal zones don't distinguish CPU/GPU; consumers only surface the
                        // "Cpu"/"Gpu"/"Board" categories, so map every zone to "Board" to keep it
                        // visible in /status and threshold checks.
                        readings.Add(new SensorReading(name, "Board", celsius));
                    }
                }

                if (_loggedFailure)
                {
                    _logger?.LogInformation("Temperature (WMI) collection recovered.");
                    _loggedFailure = false;
                }
                return Task.FromResult(
                    CollectorResult<TemperatureSnapshot>.Ok(new TemperatureSnapshot(readings), now));
            }
            catch (Exception ex)
            {
                // MSAcpi_ThermalZoneTemperature is frequently not implemented (especially on
                // desktops): degrade gracefully rather than crash. Log the first failure once.
                if (!_loggedFailure)
                {
                    _logger?.LogWarning(ex, "Temperature (WMI) collection failed; sensors will be omitted until it recovers.");
                    _loggedFailure = true;
                }
                return Task.FromResult(CollectorResult<TemperatureSnapshot>.Fail(ex.Message, now));
            }
        }
    }
}

/// <summary>
/// Pure helpers for interpreting MSAcpi_ThermalZoneTemperature values. Kept platform-agnostic and
/// internal so they can be unit-tested without a live WMI provider.
/// </summary>
internal static class WmiThermalMath
{
    // Sensors report sentinel/implausible values when not ready; keep only real temperatures.
    private const double MinPlausibleCelsius = 0.0;   // exclusive: 0.0 means "no reading"
    private const double MaxPlausibleCelsius = 150.0;  // above any real PC component temperature

    // CurrentTemperature is in tenths of a Kelvin. Celsius = tenthsKelvin/10 - 273.15.
    // Returns false when the result is outside the plausible band (treated as "no reading").
    public static bool TryToCelsius(double tenthsKelvin, out double celsius)
    {
        celsius = tenthsKelvin / 10.0 - 273.15;
        return celsius > MinPlausibleCelsius && celsius <= MaxPlausibleCelsius;
    }

    // InstanceName looks like "ACPI\ThermalZone\THM0_0"; show the last path segment as the label.
    public static string CleanZoneName(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return "ACPI Thermal Zone";
        var trimmed = instanceName.TrimEnd('\\');
        var idx = trimmed.LastIndexOf('\\');
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }
}
