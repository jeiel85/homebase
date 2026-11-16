using System.IO;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// The tray's own diagnostic log, <c>%ProgramData%\Homebase\logs\tray.log</c> (size-capped by
/// <see cref="RollingLogFile"/>), for state that otherwise can't be seen on a user's PC: the update
/// flow (checks, the skip/auto-attempt markers, the unattended install's outcome) and first-run
/// onboarding. Before this, marker read/write failures went only to <c>Debug.WriteLine</c> (issue #10).
/// Best-effort: it never throws.
/// </summary>
internal static class TrayLog
{
    private static readonly Lazy<RollingLogFile?> Target = new Lazy<RollingLogFile?>(Open);

    public static void Info(string area, string message) => Write("Information", area, message, null);

    public static void Warn(string area, string message, Exception? exception = null) =>
        Write("Warning", area, message, exception);

    private static void Write(string level, string area, string message, Exception? exception)
    {
        try
        {
            if (Target.Value is not { } file) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {area}: {message}";
            if (exception is not null) line += Environment.NewLine + exception;
            file.Append(line);
        }
        catch
        {
            // Diagnostics only — never let logging break the caller.
        }
    }

    private static RollingLogFile? Open()
    {
        try
        {
            var dir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Homebase\logs");
            Directory.CreateDirectory(dir);
            return RollingLogFile.For(Path.Combine(dir, "tray.log"));
        }
        catch
        {
            return null;
        }
    }
}
