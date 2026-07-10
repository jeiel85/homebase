using System.Text.Json.Nodes;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Persists the <c>notificationForwarding.enabled</c> flag to the Agent's admin-owned config via
/// <see cref="ElevatedConfig"/> (in-process JSON edit + one elevated copy + Agent restart).
/// </summary>
internal static class NotificationForwardingConfigurator
{
    /// <summary>
    /// Sets <c>notificationForwarding.enabled</c> and restarts the Agent so it (un)wires the pipe
    /// server. Returns <c>true</c> once applied; <c>false</c> if the user declined the UAC prompt.
    /// Throws if the existing config can't be read (so it is never clobbered).
    /// </summary>
    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        var root = await ElevatedConfig.ReadAsync();

        if (root["notificationForwarding"] is not JsonObject forwarding)
        {
            forwarding = new JsonObject();
            root["notificationForwarding"] = forwarding;
        }
        forwarding["enabled"] = enabled;
        // First time the section is created, default to forwarding everything except an explicit
        // block list, so an enabled feature has sensible behaviour without further config.
        forwarding["mode"] ??= "BlockList";
        // Default masking ON: redact OTP codes, passwords and tokens before a notification leaves
        // the machine. Only sets the default when absent, so an explicit user choice is preserved.
        forwarding["maskingEnabled"] ??= true;

        return await ElevatedConfig.WriteAsync(root);
    }
}
