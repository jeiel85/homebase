using System.Text;
using System.Text.Json;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Active Claude Code sessions from <c>%USERPROFILE%\.claude\projects\*\*.jsonl</c> (DECISIONS M-9).
/// Reads only the last 64 KB of transcripts touched in the last 10 minutes and keeps only the
/// session title (user-set <c>custom-title</c>, else <c>ai-title</c>), the working directory and the
/// last-activity time. Prompt and reply text are never read into the result.
/// Results are cached per file by its last-write time, so an idle session costs one stat call.
/// </summary>
public sealed class ClaudeSessionReader
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);
    public const int TailBytes = 64 * 1024;
    public const int MaxSessions = 10;

    private readonly string _projectsDir;
    private readonly Dictionary<string, (DateTime WriteUtc, ClaudeSession? Session)> _cache = new();

    public ClaudeSessionReader(string projectsDir) => _projectsDir = projectsDir;

    public static string DefaultProjectsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public IReadOnlyList<ClaudeSession> ReadActive(DateTime nowUtc)
    {
        if (!Directory.Exists(_projectsDir)) return Array.Empty<ClaudeSession>();
        var active = new List<(DateTime, ClaudeSession)>();
        var seen = new HashSet<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_projectsDir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var write = File.GetLastWriteTimeUtc(file);
                    if (nowUtc - write > ActiveWindow) continue;
                    seen.Add(file);
                    if (!_cache.TryGetValue(file, out var cached) || cached.WriteUtc != write)
                    {
                        cached = (write, Parse(file, write));
                        _cache[file] = cached;
                    }
                    if (cached.Session is not null) active.Add((write, cached.Session));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial result is fine */ }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(stale);
        return active.OrderByDescending(a => a.Item1).Take(MaxSessions).Select(a => a.Item2).ToList();
    }

    internal static ClaudeSession? Parse(string file, DateTime writeUtc)
    {
        string tail;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            fs.ReadExactly(buf);
            tail = Encoding.UTF8.GetString(buf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        return ParseTail(tail, writeUtc);
    }

    /// <summary>Scans JSONL lines from the end; the first (partial) line of a tail is skipped.</summary>
    public static ClaudeSession? ParseTail(string tail, DateTime writeUtc)
    {
        string? custom = null, ai = null, cwd = null;
        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 0 && (custom is null || cwd is null); i--)
        {
            var line = lines[i];
            // Cheap pre-filter: only lines that can hold what we want are parsed.
            var hasTitle = line.Contains("\"customTitle\"") || line.Contains("\"aiTitle\"");
            var hasCwd = cwd is null && line.Contains("\"cwd\"");
            if (!hasTitle && !hasCwd) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (custom is null && root.TryGetProperty("customTitle", out var c) && c.ValueKind == JsonValueKind.String) custom = c.GetString();
                if (ai is null && root.TryGetProperty("aiTitle", out var a) && a.ValueKind == JsonValueKind.String) ai = a.GetString();
                if (cwd is null && root.TryGetProperty("cwd", out var w) && w.ValueKind == JsonValueKind.String) cwd = w.GetString();
            }
            catch (JsonException) { /* partial first line or non-JSON */ }
        }

        var title = !string.IsNullOrWhiteSpace(custom) ? custom : ai;
        if (string.IsNullOrWhiteSpace(title) && cwd is null) return null;
        return new ClaudeSession(
            string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : Truncate(title!.Trim(), 120),
            cwd,
            new DateTimeOffset(DateTime.SpecifyKind(writeUtc, DateTimeKind.Utc)));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
