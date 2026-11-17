using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalOpsBot.Core.Mobile;

/// <summary>One open Claude Code session as its own status file describes it (DECISIONS M-44).</summary>
public sealed record ClaudeLiveSession(
    string SessionId,
    string? Name,
    string? Cwd,
    string Status,
    string? WaitingFor,
    DateTimeOffset? StatusSince,
    string? RemoteUrl);

/// <summary>
/// Open Claude Code sessions from <c>%USERPROFILE%\.claude\sessions\&lt;pid&gt;.json</c>, which each
/// running Claude Code process keeps current (status, what it waits for, its claude.ai session).
/// The format is Claude Code's own and undocumented, so every field is optional and a file this
/// reader cannot use is skipped. A session that ended leaves its file behind, and Windows may give
/// its PID to another program later, so a file counts only while a process with that PID and the
/// recorded start time runs.
/// </summary>
public sealed class ClaudeStatusReader
{
    public const int MaxFiles = 200;
    private static readonly Regex RemoteId = new("^[A-Za-z0-9_]{8,100}$", RegexOptions.CultureInvariant);

    private readonly string _dir;
    private readonly Func<int, long?, bool> _isAlive;

    public ClaudeStatusReader(string dir, Func<int, long?, bool>? isAlive = null)
    {
        _dir = dir;
        _isAlive = isAlive ?? IsAlive;
    }

    /// <summary>
    /// Set when a status file written in the last <see cref="ClaudeSessionReader.ActiveWindow"/> lacks a
    /// field this reader needs (pid, sessionId, status) — the format is Claude Code's own, so a change
    /// shows only as sessions missing from the cards. The caller logs it once (#61).
    /// </summary>
    public string? FormatIssue { get; private set; }

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    public IReadOnlyList<ClaudeLiveSession> Read()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<ClaudeLiveSession>();
        var list = new List<ClaudeLiveSession>();
        try
        {
            // Newest first: a running session rewrites its file on every status change, while files
            // of ended sessions only age, so however many of those pile up they fall past the limit.
            var files = new DirectoryInfo(_dir).EnumerateFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFiles);
            var now = DateTime.UtcNow;
            foreach (var file in files)
            {
                string text;
                try
                {
                    using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > 64 * 1024) continue;
                    using var reader = new StreamReader(fs);
                    text = reader.ReadToEnd();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (Parse(text, _isAlive) is { } s) list.Add(s);
                // Ended sessions' files can be from older versions; only a fresh one says what the format is now.
                if (now - file.LastWriteTimeUtc <= ClaudeSessionReader.ActiveWindow && MissingFields(text) is { } missing)
                    FormatIssue = $"Claude status file {file.Name}: no {missing}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial result is fine */ }
        return list;
    }

    /// <summary>One status file; null when it is unreadable, half-written or its process is gone.</summary>
    public static ClaudeLiveSession? Parse(string json, Func<int, long?, bool> isAlive)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var pid = r.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pi) ? pi : 0;
            var sessionId = Str(r, "sessionId");
            if (pid <= 0 || string.IsNullOrWhiteSpace(sessionId)) return null;
            long? procStart = long.TryParse(Str(r, "procStart"), out var ps) ? ps : null;
            if (!isAlive(pid, procStart)) return null;

            var status = NormalizeStatus(Str(r, "status"));
            var since = Millis(r, "statusUpdatedAt") ?? Millis(r, "updatedAt");
            var bridge = Str(r, "bridgeSessionId");
            return new ClaudeLiveSession(
                sessionId!,
                Trim(Str(r, "name"), 120),
                Str(r, "cwd"),
                status,
                status == AgentStatus.Waiting ? Trim(Str(r, "waitingFor"), 200) : null,
                since,
                bridge is not null && RemoteId.IsMatch(bridge) ? "https://claude.ai/code/" + bridge : null);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The fields <see cref="Parse"/> relies on that a complete status file lacks ("pid, status"); null
    /// when all are there, or when the text is not a whole JSON object (half-written: nothing to say).
    /// </summary>
    public static string? MissingFields(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var missing = new List<string>();
            if (!r.TryGetProperty("pid", out var p) || p.ValueKind != JsonValueKind.Number) missing.Add("pid");
            if (Str(r, "sessionId") is null) missing.Add("sessionId");
            if (Str(r, "status") is null) missing.Add("status");
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Claude Code writes busy, waiting, idle (and shell while a command runs, which is work too).</summary>
    public static string NormalizeStatus(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "idle" => AgentStatus.Idle,
        "waiting" => AgentStatus.Waiting,
        _ => AgentStatus.Busy,
    };

    /// <summary>
    /// The process runs and, when the file recorded it, started at that time (a FILETIME, within a
    /// second) — otherwise the PID belongs to a later program.
    /// </summary>
    public static bool IsAlive(int pid, long? procStartFileTimeUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            return procStartFileTimeUtc is not { } start || Math.Abs(p.StartTime.ToFileTimeUtc() - start) < 10_000_000;
        }
        catch (ArgumentException) { return false; }          // no such process
        catch (InvalidOperationException) { return false; }   // exited meanwhile
        catch (System.ComponentModel.Win32Exception) { return true; } // runs, but its start time is not ours to read
    }

    private static DateTimeOffset? Millis(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
}
