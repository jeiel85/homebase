namespace LocalOpsBot.Core.Monitoring;

/// <summary>
/// Which backend reads temperature sensors.
/// </summary>
public enum TemperatureSource
{
    /// <summary>
    /// WMI ACPI thermal zones (MSAcpi_ThermalZoneTemperature). Loads no kernel driver, so there is
    /// nothing for antivirus to flag — but many desktops don't implement it, so readings may be
    /// absent. This is the default. Every reading is reported under the "Board" category.
    /// </summary>
    Wmi,

    /// <summary>
    /// LibreHardwareMonitor. Reads per-core CPU / GPU / board sensors, but opening it loads the
    /// WinRing0 kernel driver, which antivirus flags as a vulnerable driver. Opt-in only: enable
    /// with installer/enable-temperature.ps1, which also registers a Defender exclusion.
    /// </summary>
    LibreHardware,
}

/// <summary>
/// Configuration for hardware temperature collection. Bound from the "temperature" config section.
/// </summary>
public sealed class TemperatureOptions
{
    /// <summary>
    /// Master switch. When false, temperature collection is skipped entirely (no WMI query and no
    /// kernel driver). Kept for backward compatibility with the older "set enabled=false if
    /// antivirus flags the driver" mitigation.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Which sensor backend to use. Defaults to <see cref="TemperatureSource.Wmi"/> so a fresh
    /// install never loads the WinRing0 driver. Switch to <see cref="TemperatureSource.LibreHardware"/>
    /// (via installer/enable-temperature.ps1) for full per-core / GPU sensors.
    /// </summary>
    public TemperatureSource Source { get; init; } = TemperatureSource.Wmi;
}
