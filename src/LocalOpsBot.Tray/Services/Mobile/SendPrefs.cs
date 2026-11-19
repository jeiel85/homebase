using System.IO;
using System.Text.Json;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// This Windows user's choices for sending files (DECISIONS M-66), in <c>%LOCALAPPDATA%\Homebase\send.json</c>: whether
/// Explorer's "Send to" menu has Homebase, and where the last send went (picked first next time).
/// </summary>
internal static class SendPrefs
{
    private sealed record Prefs(bool SendToMenu = true, IReadOnlyList<string>? LastTargets = null);

    private static readonly object Gate = new();
    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "send.json");

    /// <summary>Explorer's "Send to › Homebase" (on unless turned off in Settings).</summary>
    public static bool SendToMenu
    {
        get => Load().SendToMenu;
        set => Save(Load() with { SendToMenu = value });
    }

    /// <summary>The device keys the last send went to.</summary>
    public static IReadOnlyList<string> LastTargets
    {
        get => Load().LastTargets ?? Array.Empty<string>();
        set => Save(Load() with { LastTargets = value.ToList() });
    }

    private static Prefs Load()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(PathName) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PathName)) ?? new Prefs() : new Prefs();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new Prefs(); // a broken file just means the defaults
            }
        }
    }

    private static void Save(Prefs prefs)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                File.WriteAllText(PathName, JsonSerializer.Serialize(prefs));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TrayLog.Warn("Files", $"Could not save send preferences: {ex.Message}");
            }
        }
    }
}
