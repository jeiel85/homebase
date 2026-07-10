using System.IO;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Two small per-user files under <c>%LOCALAPPDATA%\Homebase\</c> that drive notification app
/// selection. Both are user-writable, so tuning which apps forward needs no elevation:
/// <list type="bullet">
/// <item><c>forwarding-apps.txt</c> — the set of app display names that have sent a notification
/// (appended by the poller) so the dashboard can offer them to choose from.</item>
/// <item><c>forwarding-block.txt</c> — apps the user chose to exclude (written by the dashboard,
/// read live by <see cref="DynamicAppFilter"/>). Everything not listed forwards, including apps
/// seen for the first time — so a new app is never silently dropped.</item>
/// </list>
/// </summary>
internal static class ForwardingApps
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase");
    private static readonly string SeenPath = Path.Combine(Dir, "forwarding-apps.txt");
    private static readonly string BlockPath = Path.Combine(Dir, "forwarding-block.txt");

    private static readonly object Gate = new();

    // ── Seen apps ───────────────────────────────────────────────────────────────────────────────
    public static void RecordSeenApp(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return;
        var name = app.Trim();
        lock (Gate)
        {
            try
            {
                if (ReadLinesNoLock(SeenPath).Contains(name, StringComparer.OrdinalIgnoreCase)) return;
                Directory.CreateDirectory(Dir);
                File.AppendAllText(SeenPath, name + Environment.NewLine);
            }
            catch { /* best-effort: never let recording break the poll loop */ }
        }
    }

    public static IReadOnlyList<string> ReadSeenApps()
    {
        lock (Gate) return ReadLinesNoLock(SeenPath);
    }

    // ── Block-list (apps to exclude) ────────────────────────────────────────────────────────────
    private static string[] _blockCache = Array.Empty<string>();
    private static DateTime _blockMtime = DateTime.MinValue;

    /// <summary>The current block-list, re-read only when the file changes (cheap per-poll call).</summary>
    public static IReadOnlyCollection<string> ReadBlockList()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(BlockPath))
                {
                    _blockCache = Array.Empty<string>();
                    _blockMtime = DateTime.MinValue;
                }
                else
                {
                    var mtime = File.GetLastWriteTimeUtc(BlockPath);
                    if (mtime != _blockMtime)
                    {
                        _blockCache = ReadLinesNoLock(BlockPath).ToArray();
                        _blockMtime = mtime;
                    }
                }
            }
            catch { /* keep the last good cache */ }
            return _blockCache;
        }
    }

    public static void WriteBlockList(IEnumerable<string> apps)
    {
        var lines = apps.Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(BlockPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            // Invalidate the cache so the very next ReadBlockList reflects the write immediately.
            _blockMtime = DateTime.MinValue;
        }
    }

    private static List<string> ReadLinesNoLock(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<string>();
            return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch { return new List<string>(); }
    }
}
