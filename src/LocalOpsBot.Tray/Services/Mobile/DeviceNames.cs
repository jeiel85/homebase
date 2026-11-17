using System.IO;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// The group's aliases as this tray shows them (DECISIONS M-51): a copy of the Agent's table, which the
/// Agent sends when the tray connects and again whenever it changes — set here, on a phone, or on another
/// PC of the group. The dashboard, the notification center, Settings › Phone and the balloons all show a
/// device through <see cref="Show"/>; <see cref="SetAsync"/> asks the Agent, which shares it with the group.
/// </summary>
internal static class DeviceNames
{
    private static bool _memoryOnly;

    /// <summary>The copy of the Agent's table. Thread-safe; <see cref="DeviceAliasTable.Changed"/> fires on any thread.</summary>
    public static DeviceAliasTable Aliases { get; } = new(TimeProvider.System);

    /// <summary>The dashboard's design preview: aliases set in memory, no Agent.</summary>
    public static void UseMemoryOnly() => _memoryOnly = true;

    /// <summary>The Agent's table arrived (<c>aliases</c> on the bridge).</summary>
    public static void Apply(IReadOnlyList<DeviceAlias> all) => Aliases.Replace(all);

    /// <summary>The alias of a device (by its group <paramref name="key"/> when known, else its name), or null.</summary>
    public static string? AliasOf(string? key, string? kind, string? name) => Aliases.Resolve(key, kind, name);

    /// <summary>The alias of a device, else the name it reports.</summary>
    public static string Show(string? kind, string name, string? key = null) => Aliases.Show(key, kind, name);

    /// <summary>
    /// Names the device <paramref name="key"/> for the whole group; an empty <paramref name="input"/> goes
    /// back to its own name. False when the Agent is not reachable or too old to share aliases.
    /// </summary>
    public static async Task<bool> SetAsync(string key, string name, string? input)
    {
        if (_memoryOnly)
        {
            Aliases.Set(key, name, input);
            return true;
        }
        if (MobileHost.Client is not { } client) return false;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpAliasesSet, new AliasSetRequest(key, name, input ?? ""));
            return (bool?)r["ok"] == true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
