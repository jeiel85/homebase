namespace LocalOpsBot.Tray.Services;

/// <summary>
/// A per-user note of one release version, kept in LocalApplicationData (writable without
/// elevation) next to the onboarding marker. The update flow keeps two of them.
/// </summary>
internal sealed class UpdateVersionMarker
{
    /// <summary>The version the user chose to skip in the update dialog.</summary>
    public static readonly UpdateVersionMarker Skipped = new("update-skipped-version.txt");

    /// <summary>
    /// The version the startup countdown last started installing on its own. If that version is
    /// still the one on offer at the next launch, the unattended install didn't take, so the dialog
    /// waits for a click instead of counting down again (no update → restart → update loop).
    /// </summary>
    public static readonly UpdateVersionMarker AutoAttempted = new("update-auto-attempted-version.txt");

    private readonly string _fileName;

    private UpdateVersionMarker(string fileName) => _fileName = fileName;

    private string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Homebase", _fileName);

    public string? Read()
    {
        try
        {
            var path = FilePath;
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Couldn't read {_fileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Stores <paramref name="version"/>, or clears the marker when it is null.</summary>
    public void Write(Version? version)
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
            // Losing a marker only means the dialog asks again (skip) or counts down once more
            // (auto-attempt) at the next launch — annoying, not harmful.
            System.Diagnostics.Debug.WriteLine($"Couldn't save {_fileName}: {ex.Message}");
        }
    }
}
