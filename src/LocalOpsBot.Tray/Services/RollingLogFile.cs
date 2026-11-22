using System.Collections.Concurrent;
using System.IO;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// An append-only log file with a size cap. Once the live file reaches <see cref="MaxBytes"/> it is
/// renamed to <c>name.1.log</c> (an older <c>name.1.log</c> becomes <c>name.2.log</c>, and the oldest
/// is dropped), so a log stays under about <c>MaxBytes × KeepFiles</c> on disk. The Agent's Serilog
/// files roll daily and keep 14; the tray has no Serilog, and its forwarding log once grew to 14.8 MB
/// as a single file (issue #4). Thread-safe within the process and never throws into the caller.
/// </summary>
internal sealed class RollingLogFile
{
    public const long MaxBytes = 5L * 1024 * 1024;

    /// <summary>The live file plus two rolled ones.</summary>
    public const int KeepFiles = 3;

    private static readonly ConcurrentDictionary<string, RollingLogFile> Files = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private readonly string _path;

    private RollingLogFile(string path) => _path = path;

    /// <summary>The one writer for <paramref name="path"/>, shared so two loggers never race a roll.</summary>
    public static RollingLogFile For(string path) =>
        Files.GetOrAdd(Path.GetFullPath(path), p => new RollingLogFile(p));

    /// <summary>Appends one line (a newline is added). False when it could not be written.</summary>
    public bool Append(string line)
    {
        lock (_gate)
        {
            try { RollIfFull(); }
            catch { /* a failed roll only means the live file keeps growing until the next try */ }
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
                return true;
            }
            catch { return false; /* logging must never throw into the caller */ }
        }
    }

    private void RollIfFull()
    {
        var live = new FileInfo(_path);
        if (!live.Exists || live.Length < MaxBytes) return;
        // name.1 → name.2 (replacing the oldest), then name → name.1.
        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var from = i == 1 ? _path : Rolled(i - 1);
            if (File.Exists(from)) File.Move(from, Rolled(i), overwrite: true);
        }
    }

    private string Rolled(int n) => Path.Combine(
        Path.GetDirectoryName(_path) ?? string.Empty,
        $"{Path.GetFileNameWithoutExtension(_path)}.{n}{Path.GetExtension(_path)}");
}
