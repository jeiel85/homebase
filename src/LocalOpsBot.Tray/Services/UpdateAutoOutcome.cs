using System.Globalization;
using System.IO;

namespace LocalOpsBot.Tray.Services;

/// <summary>Where an unattended install (the startup countdown ran out) ended.</summary>
internal enum AutoInstallResult
{
    /// <summary>Downloaded and verified; the administrator prompt is up. Still this at the next launch: never approved.</summary>
    AwaitingApproval,

    /// <summary>The administrator prompt was declined or dismissed without an answer (ERROR_CANCELLED).</summary>
    NotApproved,

    /// <summary>The elevated installer was launched; the next launch shows whether it worked.</summary>
    Started,

    /// <summary>The download or the installer launch failed.</summary>
    Failed,

    /// <summary>Someone cancelled the download, so they were there: nothing to report later.</summary>
    Cancelled,
}

internal sealed record AutoInstallRecord(Version Version, AutoInstallResult Result, DateTimeOffset At);

/// <summary>
/// The outcome of the last unattended install, kept next to the auto-attempt marker
/// (<see cref="UpdateVersionMarker.AutoAttempted"/>) in LocalApplicationData. The marker alone only
/// stops the next countdown; this records why, so the next update popup can tell the user that an
/// install started while they were away and where it stopped (issue #18), and tray.log gets the
/// result either way. One line: <c>version|result|timestamp</c>.
/// </summary>
internal static class UpdateAutoOutcome
{
    private const string FileName = "update-auto-outcome.txt";

    /// <summary>The record's file; tests point it elsewhere so they never touch the user's own.</summary>
    internal static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", FileName);

    public static void Write(Version version, AutoInstallResult result)
    {
        TrayLog.Info("Update", $"Unattended install of v{Display(version)}: {result}");
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("|",
                version.ToString(), result.ToString(), DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)));
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Update", $"Couldn't save {FileName}: {ex.Message}");
        }
    }

    public static AutoInstallRecord? Read()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Trim().Split('|');
            if (parts.Length == 3
                && Version.TryParse(parts[0], out var version)
                && Enum.TryParse<AutoInstallResult>(parts[1], out var result)
                && DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                return new AutoInstallRecord(version, result, at);
            return null;
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Update", $"Couldn't read {FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The line for the update popup when the last unattended install of <paramref name="available"/>
    /// didn't take; null when there is nothing to say (no attempt, another version, or cancelled by hand).
    /// </summary>
    public static string? NoteFor(Version available)
    {
        if (Read() is not { } r || Normalize(r.Version) != Normalize(available)) return null;
        var v = Display(r.Version);
        var at = r.At.ToLocalTime();
        return r.Result switch
        {
            AutoInstallResult.AwaitingApproval or AutoInstallResult.NotApproved => L.UpdAutoNotApproved(v, at),
            AutoInstallResult.Failed => L.UpdAutoFailed(v, at),
            AutoInstallResult.Started => L.UpdAutoNotTaken(v, at),
            _ => null,
        };
    }

    /// <summary>
    /// At startup: an attempt for a version that is installed now is settled, so log how it ended and
    /// forget it. An attempt for a version still on offer stays for <see cref="NoteFor"/>.
    /// </summary>
    public static void Reconcile(string currentVersion)
    {
        if (Read() is not { } r) return;
        if (!Version.TryParse(currentVersion, out var current) || Normalize(current) < Normalize(r.Version)) return;
        TrayLog.Info("Update", r.Result == AutoInstallResult.Started
            ? $"Unattended install of v{Display(r.Version)} succeeded; running v{currentVersion}"
            : $"Unattended install of v{Display(r.Version)} ended as {r.Result}; v{currentVersion} is installed now");
        try
        {
            var path = FilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Update", $"Couldn't clear {FileName}: {ex.Message}");
        }
    }

    // Releases are major.minor.patch; compare and show them that way whatever the stored precision.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string Display(Version v) => Normalize(v).ToString(3);
}
