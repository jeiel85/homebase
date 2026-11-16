using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Active Codex sessions (DECISIONS M-26, M-44) from <c>$CODEX_HOME</c> or <c>%USERPROFILE%\.codex</c>:
/// <c>sessions/YYYY/MM/DD/rollout-…-&lt;id&gt;.jsonl</c> files written in the last 10 minutes.
/// The title comes from <c>session_index.jsonl</c> (<c>thread_name</c>). From the rollout itself it
/// keeps the working directory, the model and reasoning effort, token counts (context in use, the
/// model's window, the session total), whether a turn is running and the last tool's name.
/// A rollout is read once (its last <see cref="MaxInitialRead"/> bytes when larger) and then only
/// the lines appended since, because one line can be megabytes and the turn's settings sit early in
/// the turn. Prompt, reply and tool-argument text are never read into the result.
/// </summary>
public sealed class CodexSessionReader
{
    public const string AppName = "Codex";
    public const int MaxInitialRead = 8 * 1024 * 1024;

    /// <summary>How often the dated folders are searched for rollouts written lately; a new session appears within it.</summary>
    public static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(30);

    private readonly string _home;
    private readonly Dictionary<string, FileState> _files = new();
    private string[] _candidates = Array.Empty<string>();
    private DateTime _nextDiscovery = DateTime.MinValue;
    private DateTime _indexWriteUtc;
    private Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    private sealed class FileState
    {
        public DateTime WriteUtc;
        public long Offset;
        public CodexDetails Details = new();
    }

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

        var active = new List<(DateTime Write, string Id, CodexDetails Details)>();
        var seen = new HashSet<string>();
        try
        {
            // The whole tree (months of dated folders) is walked every DiscoveryInterval; in between
            // only the files already known are looked at again, so the 3-second watch stays cheap.
            if (nowUtc >= _nextDiscovery || nowUtc < _nextDiscovery - DiscoveryInterval)
            {
                _candidates = new DirectoryInfo(sessions).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                    .Where(f => nowUtc - f.LastWriteTimeUtc <= ClaudeSessionReader.ActiveWindow)
                    .Select(f => f.FullName)
                    .ToArray();
                _nextDiscovery = nowUtc + DiscoveryInterval;
            }
            foreach (var path in _candidates.Concat(_files.Keys).Distinct().ToList())
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                var write = file.LastWriteTimeUtc;
                if (nowUtc - write > ClaudeSessionReader.ActiveWindow) continue;
                seen.Add(file.FullName);
                if (!_files.TryGetValue(file.FullName, out var state))
                    _files[file.FullName] = state = new FileState();
                if (state.WriteUtc != write)
                {
                    Follow(file.FullName, state);
                    state.WriteUtc = write;
                }
                active.Add((write, SessionId(file.Name), state.Details));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial result is fine */ }

        foreach (var stale in _files.Keys.Where(k => !seen.Contains(k)).ToList()) _files.Remove(stale);
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
                    : FolderName(a.Details.Cwd) ?? "(제목 없음)",
                a.Details.Cwd,
                new DateTimeOffset(DateTime.SpecifyKind(a.Write, DateTimeKind.Utc)),
                Agent: a.Details.ToInfo(a.Id)))
            .ToList();
    }

    /// <summary>Reads the bytes appended since the last call, up to the last complete line.</summary>
    private static void Follow(string path, FileState state)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < state.Offset) // rewritten from scratch
            {
                state.Offset = 0;
                state.Details = new CodexDetails();
            }
            var start = state.Offset;
            var skipPartial = false;
            if (start == 0 && fs.Length > MaxInitialRead)
            {
                start = fs.Length - MaxInitialRead;
                skipPartial = true;
            }
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            fs.ReadExactly(buf);
            var complete = Array.LastIndexOf(buf, (byte)'\n') + 1;
            if (complete == 0) return;
            var first = skipPartial ? Array.IndexOf(buf, (byte)'\n') + 1 : 0;
            if (first < complete)
                state.Details = ParseLines(Encoding.UTF8.GetString(buf, first, complete - first), state.Details);
            state.Offset = start + complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Folds rollout lines into <paramref name="previous"/>. Only lines whose type is one we use are
    /// parsed, so the megabyte-long ones (world state, tool output) cost a substring search.
    /// </summary>
    public static CodexDetails ParseLines(string text, CodexDetails previous)
    {
        var d = previous;
        foreach (var line in text.Split('\n'))
        {
            var isContext = line.Contains("\"type\":\"turn_context\"") || line.Contains("\"type\":\"session_meta\"");
            var isTokens = line.Contains("\"type\":\"token_count\"");
            var isLifecycle = line.Contains("\"type\":\"task_started\"") || line.Contains("\"type\":\"task_complete\"")
                              || line.Contains("\"type\":\"turn_aborted\"");
            var isTool = line.Contains("\"type\":\"function_call\"") || line.Contains("\"type\":\"custom_tool_call\"");
            if (!isContext && !isTokens && !isLifecycle && !isTool) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                var type = Str(root, "type");
                var inner = Str(payload, "type");
                DateTimeOffset? ts = DateTimeOffset.TryParse(Str(root, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                if (type == "turn_context")
                {
                    d = d with
                    {
                        Cwd = Str(payload, "cwd") ?? d.Cwd,
                        Model = Str(payload, "model") ?? d.Model,
                        Effort = Str(payload, "effort") ?? d.Effort,
                    };
                }
                else if (type == "session_meta")
                {
                    d = d with { Cwd = d.Cwd ?? Str(payload, "cwd") };
                }
                else if (type == "event_msg" && inner == "token_count"
                         && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    var last = info.TryGetProperty("last_token_usage", out var l) ? l : default;
                    var total = info.TryGetProperty("total_token_usage", out var t) ? t : default;
                    d = d with
                    {
                        ContextTokens = Long(last, "input_tokens") ?? d.ContextTokens,
                        ContextWindow = Long(info, "model_context_window") ?? d.ContextWindow,
                        TotalTokens = Long(total, "total_tokens") ?? d.TotalTokens,
                    };
                }
                else if (type == "event_msg" && inner is "task_started" or "task_complete" or "turn_aborted")
                {
                    d = d with { Running = inner == "task_started", StatusSince = ts ?? d.StatusSince };
                }
                else if (type == "response_item" && inner is "function_call" or "custom_tool_call"
                         && Str(payload, "name") is { } name)
                {
                    d = d with { LastTool = WorkCatalog.Truncate(name, 80) };
                }
            }
            catch (JsonException) { }
        }
        return d;
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

    /// <summary>
    /// The newest <c>cwd</c> in a piece of rollout: <c>turn_context</c> lines carry it for every turn,
    /// <c>session_meta</c> for the first. A partial first line fails to parse and is skipped.
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

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
}

/// <summary>What <see cref="CodexSessionReader"/> has gathered about one rollout so far.</summary>
public sealed record CodexDetails(
    string? Cwd = null,
    string? Model = null,
    string? Effort = null,
    long? ContextTokens = null,
    long? ContextWindow = null,
    long? TotalTokens = null,
    bool Running = false,
    DateTimeOffset? StatusSince = null,
    string? LastTool = null)
{
    /// <summary>A running turn is busy; a finished or aborted one idle; a rollout with neither yet, unknown.</summary>
    public AgentInfo ToInfo(string sessionId) => new(
        Running ? AgentStatus.Busy : StatusSince is null ? null : AgentStatus.Idle,
        StatusSince: StatusSince,
        Model: Model,
        Effort: Effort,
        ContextTokens: ContextTokens,
        ContextWindow: ContextWindow,
        TotalTokens: TotalTokens,
        LastTool: LastTool,
        SessionId: sessionId);
}
