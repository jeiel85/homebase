using System.Text;
using System.Text.Json;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Active Claude Code sessions from <c>%USERPROFILE%\.claude\projects\*\*.jsonl</c> (DECISIONS M-9, M-44).
/// Reads only the last 64 KB of transcripts touched in the last 10 minutes and keeps the session
/// title (user-set <c>custom-title</c>, else <c>ai-title</c>), the working directory, the
/// last-activity time and, from the newest assistant entry, the model, how many tokens its request
/// sent (context use) and the name of the last tool it called; also the permission mode and the
/// pull request the session linked. Prompt, reply and tool-argument text are never read into the result.
/// Results are cached per file by its last-write time, so an idle session costs one stat call.
/// <see cref="ReadActive"/> merges in the live status of <see cref="ClaudeStatusReader"/>.
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

    /// <param name="live">Open sessions from their status files; null when they are not read.</param>
    public IReadOnlyList<ClaudeSession> ReadActive(DateTime nowUtc, IReadOnlyList<ClaudeLiveSession>? live = null) =>
        Merge(ReadTranscripts(nowUtc), live);

    /// <summary>The sessions whose transcript changed in the last <see cref="ActiveWindow"/>, without live status.</summary>
    public IReadOnlyList<ClaudeSession> ReadTranscripts(DateTime nowUtc)
    {
        var active = new List<ClaudeSession>();
        var seen = new HashSet<string>();
        if (Directory.Exists(_projectsDir))
        {
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
                        if (cached.Session is not null) active.Add(cached.Session);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial result is fine */ }
        }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(stale);
        return active;
    }

    /// <summary>
    /// Adds each transcript's live status (matched by session id), then the open sessions whose
    /// transcript went quiet longer than <see cref="ActiveWindow"/> but which still work or wait for
    /// the user — a permission prompt left overnight is exactly what the phone should show.
    /// Newest first, at most <see cref="MaxSessions"/>.
    /// </summary>
    public static IReadOnlyList<ClaudeSession> Merge(IEnumerable<ClaudeSession> transcripts, IReadOnlyList<ClaudeLiveSession>? live)
    {
        var byId = new Dictionary<string, ClaudeLiveSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in live ?? Array.Empty<ClaudeLiveSession>()) byId.TryAdd(l.SessionId, l);

        var result = new List<ClaudeSession>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in transcripts)
        {
            var id = s.Agent?.SessionId;
            if (id is not null && byId.TryGetValue(id, out var l))
            {
                matched.Add(id);
                result.Add(s with
                {
                    Agent = (s.Agent ?? new AgentInfo()) with
                    {
                        Status = l.Status,
                        WaitingFor = l.WaitingFor,
                        StatusSince = l.StatusSince,
                        RemoteUrl = l.RemoteUrl,
                    },
                });
            }
            else result.Add(s);
        }
        foreach (var l in byId.Values)
        {
            if (matched.Contains(l.SessionId) || l.Status == AgentStatus.Idle) continue;
            result.Add(new ClaudeSession(
                l.Name ?? FolderName(l.Cwd) ?? "(제목 없음)",
                l.Cwd,
                l.StatusSince ?? DateTimeOffset.UnixEpoch,
                new AgentInfo(l.Status, l.WaitingFor, l.StatusSince, RemoteUrl: l.RemoteUrl, SessionId: l.SessionId)));
        }
        return result.OrderByDescending(s => s.LastActivity).Take(MaxSessions).ToList();
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

        // The transcript is named after its session id, which the status file carries too.
        return ParseTail(tail, writeUtc, Path.GetFileNameWithoutExtension(file));
    }

    /// <summary>Scans JSONL lines from the end; the first (partial) line of a tail fails to parse and is skipped.</summary>
    public static ClaudeSession? ParseTail(string tail, DateTime writeUtc, string? sessionId = null)
    {
        string? custom = null, ai = null, cwd = null, model = null, lastTool = null, mode = null;
        long? context = null;
        int? pr = null;
        bool modeSeen = false, usageSeen = false;
        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            // Cheap pre-filters: only lines that can hold what we still want are parsed.
            var hasTitle = custom is null && (line.Contains("\"customTitle\"") || line.Contains("\"aiTitle\""));
            var hasCwd = cwd is null && line.Contains("\"cwd\"");
            var isAssistant = (!usageSeen || lastTool is null) && line.Contains("\"type\":\"assistant\"")
                              && !line.Contains("\"isSidechain\":true");
            var isMode = !modeSeen && line.Contains("\"type\":\"mode\"");
            var isPr = pr is null && line.Contains("\"type\":\"pr-link\"");
            if (!hasTitle && !hasCwd && !isAssistant && !isMode && !isPr) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (custom is null && Str(root, "customTitle") is { } c) custom = c;
                if (ai is null && Str(root, "aiTitle") is { } a) ai = a;
                if (cwd is null && Str(root, "cwd") is { } w) cwd = w;
                if (isMode)
                {
                    modeSeen = true;
                    mode = Str(root, "mode") is { } m && m is not ("default" or "normal") ? m : null;
                }
                if (isPr && root.TryGetProperty("prNumber", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var num) && num > 0)
                    pr = num;
                if (isAssistant && root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                    ReadAssistant(msg, ref usageSeen, ref model, ref context, ref lastTool);
            }
            catch (JsonException) { /* partial first line or non-JSON */ }
        }

        var title = !string.IsNullOrWhiteSpace(custom) ? custom : ai;
        if (string.IsNullOrWhiteSpace(title) && cwd is null) return null;
        AgentInfo? agent = model is null && context is null && lastTool is null && mode is null && pr is null && sessionId is null
            ? null
            : new AgentInfo(Model: model, ContextTokens: context, LastTool: lastTool, Mode: mode, PrNumber: pr, SessionId: sessionId);
        return new ClaudeSession(
            string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : Truncate(title!.Trim(), 120),
            cwd,
            new DateTimeOffset(DateTime.SpecifyKind(writeUtc, DateTimeKind.Utc)),
            agent);
    }

    /// <summary>
    /// The newest assistant entry with usage gives the model and the context in use (everything the
    /// request sent: new, cache-written and cache-read input). Claude Code's own placeholder entries
    /// ("&lt;synthetic&gt;") carry no real usage and are passed over. The last tool is the newest
    /// <c>tool_use</c> block's name, in whichever entry it is.
    /// </summary>
    private static void ReadAssistant(JsonElement msg, ref bool usageSeen, ref string? model, ref long? context, ref string? lastTool)
    {
        var m = Str(msg, "model");
        if (!usageSeen && m is not null && m != "<synthetic>"
            && msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            usageSeen = true;
            model = m;
            var total = (Long(u, "input_tokens") ?? 0) + (Long(u, "cache_creation_input_tokens") ?? 0) + (Long(u, "cache_read_input_tokens") ?? 0);
            context = total > 0 ? total : null;
        }
        if (lastTool is null && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            for (var k = content.GetArrayLength() - 1; k >= 0; k--)
            {
                var block = content[k];
                if (block.ValueKind == JsonValueKind.Object && Str(block, "type") == "tool_use" && Str(block, "name") is { } name)
                {
                    lastTool = Truncate(name, 80);
                    break;
                }
            }
        }
    }

    private static string? FolderName(string? cwd) =>
        string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : cwd;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
