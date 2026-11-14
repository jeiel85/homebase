using System.Text;
using System.Text.Json;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Active Codex sessions (DECISIONS M-26) from <c>$CODEX_HOME</c> or <c>%USERPROFILE%\.codex</c>:
/// <c>sessions/YYYY/MM/DD/rollout-…-&lt;id&gt;.jsonl</c> files written in the last 10 minutes.
/// Like <see cref="ClaudeSessionReader"/> it reads only the last 64 KB of each transcript and keeps
/// only the working directory; the title comes from <c>session_index.jsonl</c> (<c>thread_name</c>).
/// Prompt and reply text are never read into the result.
/// </summary>
public sealed class CodexSessionReader
{
    public const string AppName = "Codex";

    private readonly string _home;
    private readonly Dictionary<string, (DateTime WriteUtc, string? Cwd)> _cache = new();
    private DateTime _indexWriteUtc;
    private Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    public CodexSessionReader(string home) => _home = home;

    public static string DefaultHome
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            return !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
    }

    public IReadOnlyList<WorkItem> ReadActive(DateTime nowUtc)
    {
        var sessions = Path.Combine(_home, "sessions");
        if (!Directory.Exists(sessions)) return Array.Empty<WorkItem>();

        var active = new List<(DateTime Write, string Id, string? Cwd)>();
        var seen = new HashSet<string>();
        try
        {
            foreach (var file in new DirectoryInfo(sessions).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var write = file.LastWriteTimeUtc;
                if (nowUtc - write > ClaudeSessionReader.ActiveWindow) continue;
                seen.Add(file.FullName);
                if (!_cache.TryGetValue(file.FullName, out var cached) || cached.WriteUtc != write)
                {
                    cached = (write, ReadCwd(file.FullName));
                    _cache[file.FullName] = cached;
                }
                active.Add((write, SessionId(file.Name), cached.Cwd));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial result is fine */ }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(stale);
        if (active.Count == 0) return Array.Empty<WorkItem>();

        var titles = Titles();
        return active
            .OrderByDescending(a => a.Write)
            .Take(ClaudeSessionReader.MaxSessions)
            .Select(a => new WorkItem(
                WorkKind.Agent,
                AppName,
                titles.TryGetValue(a.Id, out var t) && !string.IsNullOrWhiteSpace(t)
                    ? WorkCatalog.Truncate(t.Trim(), 120)
                    : FolderName(a.Cwd) ?? "(제목 없음)",
                a.Cwd,
                new DateTimeOffset(DateTime.SpecifyKind(a.Write, DateTimeKind.Utc))))
            .ToList();
    }

    /// <summary><c>rollout-2026-11-13T17-04-49-&lt;uuid&gt;.jsonl</c> → the uuid (36 chars).</summary>
    internal static string SessionId(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length >= 36 ? stem[^36..] : stem;
    }

    private static string? FolderName(string? cwd) =>
        string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : cwd;

    private Dictionary<string, string> Titles()
    {
        var path = Path.Combine(_home, "session_index.jsonl");
        try
        {
            var write = File.GetLastWriteTimeUtc(path);
            if (write == _indexWriteUtc) return _titles;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            _titles = ParseIndex(reader.ReadToEnd());
            _indexWriteUtc = write;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return _titles;
    }

    /// <summary>Later lines win: a renamed thread appends a new line with the same id.</summary>
    public static Dictionary<string, string> ParseIndex(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("\"thread_name\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("thread_name", out var name) && name.ValueKind == JsonValueKind.String)
                    map[id.GetString()!] = name.GetString()!;
            }
            catch (JsonException) { }
        }
        return map;
    }

    private static string? ReadCwd(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - ClaudeSessionReader.TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            fs.ReadExactly(buf);
            return ParseCwd(Encoding.UTF8.GetString(buf));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The newest <c>cwd</c> in the tail: <c>turn_context</c> lines carry it for every turn,
    /// <c>session_meta</c> for the first. The first (partial) line of a tail is skipped.
    /// </summary>
    public static string? ParseCwd(string tail)
    {
        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"cwd\"") || !(line.Contains("\"turn_context\"") || line.Contains("\"session_meta\""))) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                    return cwd.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }
}
