namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Tracks whether the first-run onboarding has already been shown. The marker lives in
/// the per-user LocalApplicationData folder, which is writable without elevation — unlike
/// the admin-owned ProgramData config the Agent reads — so each Windows user is walked
/// through onboarding exactly once and can always reopen it from the tray menu.
/// </summary>
internal static class OnboardingState
{
    private static string MarkerPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Homebase", "onboarding.done");

    public static bool IsCompleted()
    {
        try { return System.IO.File.Exists(MarkerPath); }
        catch { return false; }
    }

    public static void MarkCompleted()
    {
        try
        {
            var path = MarkerPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, DateTimeOffset.Now.ToString("o"));
        }
        catch
        {
            // Best-effort: if the flag can't be persisted, onboarding just shows again next
            // launch — mildly annoying but harmless, and the tray menu still reopens it.
        }
    }
}
