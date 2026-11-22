using System.IO;
using System.Text;

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

    // For Error's last resort: set aside before anything goes wrong, so writing one line then needs almost no
    // new memory (an error from running out of it left no line at all, #106).
    private static readonly char[] EmergencyChars = new char[1024];
    private static readonly byte[] EmergencyBytes = new byte[4096];
    private static readonly object EmergencyGate = new();
    private static string? _path;

    public static void Info(string area, string message) => Write("Information", area, message, null);

    public static void Warn(string area, string message, Exception? exception = null) =>
        Write("Warning", area, message, exception);

    /// <summary>
    /// An error nothing caught. Written like <see cref="Warn"/> with the whole exception; when that fails — typically
    /// because memory ran out, which is often why the error happened — one line with the exception's type and message
    /// is written from buffers set aside beforehand (<see cref="Prepare"/>).
    /// </summary>
    public static void Error(string area, string message, Exception? exception)
    {
        if (Write("Error", area, message, exception)) return;
        WriteEmergency(area, message, exception);
    }

    /// <summary>Works out the log's path ahead, at start-up, for <see cref="Error"/>'s last resort.</summary>
    public static void Prepare()
    {
        try { _path ??= LogPath(); }
        catch { /* the normal path tries again */ }
    }

    private static bool Write(string level, string area, string message, Exception? exception)
    {
        try
        {
            if (Target.Value is not { } file) return false;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {area}: {message}";
            if (exception is not null) line += Environment.NewLine + exception;
            return file.Append(line);
        }
        catch
        {
            // Diagnostics only — never let logging break the caller.
            return false;
        }
    }

    /// <summary>
    /// One line without building strings: the parts are copied into <see cref="EmergencyChars"/> (cut at its end) and
    /// encoded into <see cref="EmergencyBytes"/>, then appended through a bare file handle. Visible for tests.
    /// </summary>
    internal static bool WriteEmergency(string area, string message, Exception? exception)
    {
        lock (EmergencyGate)
        {
            try
            {
                if (_path is not { } path) return false;
                var chars = EmergencyChars.AsSpan();
                // The text fills all but the last two chars, so a line cut at the buffer still ends with its line break.
                var body = chars[..^2];
                var n = 0;
                if (DateTime.Now.TryFormat(body, out var written, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
                    n = written;
                Put(body, ref n, " [Error] ");
                Put(body, ref n, area);
                Put(body, ref n, ": ");
                Put(body, ref n, message);
                if (exception is not null)
                {
                    Put(body, ref n, " — ");
                    string? type = null;
                    try { type = exception.GetType().FullName; } catch { /* the type's name could not be had */ }
                    Put(body, ref n, type ?? "exception");
                    Put(body, ref n, ": ");
                    Put(body, ref n, exception.Message);
                }
                Put(body, ref n, " (written without its stack trace: the full entry could not be written)");
                chars[n++] = '\r';
                chars[n++] = '\n';
                var bytes = Encoding.UTF8.GetBytes(chars[..n], EmergencyBytes);
                using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                RandomAccess.Write(handle, EmergencyBytes.AsSpan(0, bytes), RandomAccess.GetLength(handle));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void Put(Span<char> into, ref int at, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var take = Math.Min(text.Length, into.Length - at);
        if (take <= 0) return;
        text.AsSpan(0, take).CopyTo(into[at..]);
        at += take;
    }

    private static string LogPath()
    {
        var dir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Homebase\logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "tray.log");
    }

    private static RollingLogFile? Open()
    {
        try
        {
            _path ??= LogPath();
            return RollingLogFile.For(_path);
        }
        catch
        {
            return null;
        }
    }
}
