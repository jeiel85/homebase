using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Shared read access to the same configuration the Agent sees: the installer-written
/// ProgramData appsettings.json plus HOMEBASE__ environment overrides. Rebuilt per call so it
/// reflects the current on-disk/env state. Read-only — config writes go through the elevated
/// helpers (configure-telegram.ps1, <see cref="NotificationForwardingConfigurator"/>).
/// </summary>
internal static class TrayConfig
{
    public const string ConfigPath = @"C:\ProgramData\Homebase\config\appsettings.json";

    // Mirror the Agent's config sources: the ProgramData file, then HOMEBASE__ env overrides.
    public static IConfigurationRoot Load() =>
        new ConfigurationBuilder()
            .AddJsonFile(ConfigPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("HOMEBASE__")
            .Build();

    /// <summary>
    /// The effective <c>notificationForwarding:enabled</c> flag — the ProgramData JSON value,
    /// overridden by a HOMEBASE__ environment variable if one is set. Defaults to false.
    /// </summary>
    public static bool IsNotificationForwardingEnabled() =>
        LocalOpsBot.Core.Notifications.NotificationForwardingConfig.IsEnabled(Load());
}
