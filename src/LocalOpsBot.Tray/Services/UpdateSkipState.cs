namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Remembers the version the user chose to skip in the update dialog. Per-user, in
/// LocalApplicationData (writable without elevation), next to the onboarding marker.
/// </summary>
internal static class UpdateSkipState
{
    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Homebase", "update-skipped-version.txt");

    public static string? Read()
    {
        try
        {
            var path = FilePath;
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Couldn't read the skipped update version: {ex.Message}");
            return null;
        }
    }

    /// <summary>Stores <paramref name="version"/>, or clears the skip when it is null.</summary>
    public static void Write(Version? version)
    {
        try
        {
            var path = FilePath;
            if (version is null)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, version.ToString(3));
        }
        catch (Exception ex)
        {
            // If it can't be saved, the dialog just shows again next launch — annoying, not harmful.
            System.Diagnostics.Debug.WriteLine($"Couldn't save the skipped update version: {ex.Message}");
        }
    }
}
