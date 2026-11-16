using System.Security.Cryptography;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Protocol.Messaging;

namespace LocalOpsBot.Core.Mobile;

/// <summary>Answers the phone can give (DECISIONS M-48).</summary>
public static class ApprovalDecision
{
    public const string Allow = "allow";
    public const string Deny = "deny";

    public static bool IsKnown(string? d) => d is Allow or Deny;
}

/// <summary>
/// A Claude Code permission prompt sent to the phone (DECISIONS M-48): which tool, on what, in which
/// session. Unlike the work tab (M-44) it carries the tool's argument — the command, the file — because
/// that is what the user decides on; it only ever leaves the PC when the user turned phone approvals on.
/// </summary>
public sealed record ApprovalRequest(
    string Id,
    string App,
    string Session,
    string? Project,
    string ToolTitle,
    string Detail,
    DateTimeOffset ExpiresAt);

/// <summary>Body of <c>POST /api/v1/approval</c>.</summary>
public sealed record ApprovalAnswer(int Schema, string Id, string Decision);

/// <summary>Response of the bridge op <c>approval.ask</c>: the phone's decision, or null for "ask on the PC".</summary>
public sealed record ApprovalAskResult(string? Decision);

/// <summary>
/// Claude Code's <c>PermissionRequest</c> hook as Homebase uses it (DECISIONS M-48): reading the hook's
/// input, writing its output, and adding or removing the hook in <c>~/.claude/settings.json</c>.
/// Pure (no files, no network) so tests pin it; the Tray does the I/O.
/// </summary>
public static class ClaudeHook
{
    /// <summary>The Tray's loopback path the hook posts to; also how our entry is recognised in settings.json.</summary>
    public const string Path = "/claude/permission";
    public const string TokenHeader = "X-Homebase-Token";

    /// <summary>How long a prompt waits for the phone before Claude Code asks on the PC as usual.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(10);

    public const int MaxDetail = 3000;

    /// <summary>
    /// The prompt for the phone, or null when the phone has nothing to decide: unreadable input, or
    /// a question (AskUserQuestion) — "allow" would only reopen the same question on the PC.
    /// </summary>
    public static ApprovalRequest? FromInput(string json, Func<string, string?> sessionTitle, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var tool = Str(r, "tool_name");
            if (string.IsNullOrWhiteSpace(tool) || tool == "AskUserQuestion") return null;
            var input = r.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object ? ti : default;
            var cwd = Str(r, "cwd");
            var sessionId = Str(r, "session_id");
            var folder = string.IsNullOrWhiteSpace(cwd) ? null : System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
            var session = (sessionId is null ? null : sessionTitle(sessionId)) ?? folder ?? "Claude Code";
            return new ApprovalRequest(
                NewId(),
                ClaudeSession.AppName,
                Clip(session, 120),
                cwd,
                ToolTitle(tool),
                Detail(tool, input),
                now + WaitLimit);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The hook's stdout for a decision (Claude Code's PermissionRequest output).</summary>
    public static string Output(string decision) => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PermissionRequest",
            decision = decision == ApprovalDecision.Allow
                ? (object)new { behavior = "allow" }
                : new { behavior = "deny", message = "휴대폰(Homebase)에서 거부했습니다." },
        },
    });

    public static string ToolTitle(string tool) => tool switch
    {
        "Bash" or "PowerShell" => "명령 실행",
        "Edit" or "MultiEdit" => "파일 수정",
        "Write" => "파일 쓰기",
        "NotebookEdit" => "노트북 수정",
        "Read" => "파일 읽기",
        "WebFetch" => "웹 페이지 가져오기",
        "WebSearch" => "웹 검색",
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => tool[5..].Replace("__", " › ") + " 사용",
        _ => tool + " 사용",
    };

    /// <summary>What the tool would act on: the command, the file (with the start of new content), the URL, the query.</summary>
    public static string Detail(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        string? s = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Edit" or "MultiEdit" or "Read" => Str(input, "file_path"),
            "NotebookEdit" => Str(input, "notebook_path"),
            "Write" => Str(input, "file_path") is { } f ? f + "\n\n" + Head(Str(input, "content") ?? "", 12) : null,
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            _ => null,
        };
        if (tool == "Edit" && Str(input, "new_string") is { } ns) s += "\n\n" + Head(ns, 12);
        if (tool is "Bash" or "PowerShell" && Str(input, "description") is { Length: > 0 } why) s = why + "\n\n" + s;
        s ??= JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return s.Length > MaxDetail ? s[..MaxDetail] + "…" : s;
    }

    // ---- settings.json -------------------------------------------------------------------------

    /// <summary>
    /// The hook command: Windows' own curl posts the hook input to the Tray and prints its answer.
    /// When the Tray is not running the connection is refused at once, curl prints nothing and Claude
    /// Code asks on the PC as usual.
    /// </summary>
    public static string Command(int port, string token) =>
        $"curl.exe -s --max-time {(int)WaitLimit.TotalSeconds + 30} -X POST http://127.0.0.1:{port}{Path} " +
        $"-H \"{TokenHeader}: {token}\" -H \"Content-Type: application/json\" --data-binary \"@-\"";

    /// <summary>Claude Code's hook timeout (seconds), past curl's own.</summary>
    public static int TimeoutSeconds => (int)WaitLimit.TotalSeconds + 60;

    public static bool IsOurs(string? command) =>
        command is not null && command.Contains(Path, StringComparison.Ordinal) && command.Contains(TokenHeader, StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="settingsJson"/> (null or empty for no file) with our PermissionRequest hook
    /// replaced by <paramref name="command"/>, or removed when it is null. Everything else is kept;
    /// groups and the <c>hooks</c> object left empty by a removal are dropped. Throws
    /// <see cref="JsonException"/> or <see cref="InvalidDataException"/> for a file that is not a JSON object.
    /// </summary>
    public static string ApplyToSettings(string? settingsJson, string? command)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var root = string.IsNullOrWhiteSpace(settingsJson)
            ? new JsonObject()
            : JsonNode.Parse(settingsJson, documentOptions: options) as JsonObject
              ?? throw new InvalidDataException("settings.json is not a JSON object.");

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var groups = hooks["PermissionRequest"] as JsonArray ?? new JsonArray();
        foreach (var g in groups.ToList())
        {
            if (g?["hooks"] is not JsonArray hs) continue;
            foreach (var h in hs.Where(h => IsOurs((h?["command"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : null)).ToList())
                hs.Remove(h);
            if (hs.Count == 0) groups.Remove(g);
        }
        if (command is not null)
        {
            groups.Add(new JsonObject
            {
                ["matcher"] = "*",
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = TimeoutSeconds,
                }),
            });
        }
        if (groups.Count > 0) hooks["PermissionRequest"] = groups; else hooks.Remove("PermissionRequest");
        if (hooks.Count > 0) root["hooks"] = hooks; else root.Remove("hooks");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>The command of our hook in <paramref name="settingsJson"/>, or null.</summary>
    public static string? InstalledCommand(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            var root = JsonNode.Parse(settingsJson, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (root?["hooks"]?["PermissionRequest"] is not JsonArray groups) return null;
            foreach (var g in groups)
            {
                if (g?["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs)
                    if ((h?["command"] as JsonValue)?.TryGetValue<string>(out var c) == true && IsOurs(c)) return c;
            }
        }
        catch (JsonException) { }
        return null;
    }

    // ---- push ----------------------------------------------------------------------------------

    /// <summary>Notification category of an approval push.</summary>
    public const string Category = "agent-approval";

    /// <summary>
    /// The push: header "PC · Claude Code", title the action ("명령 실행"), body the session and what it
    /// would run. Critical so it pops up; encrypted channels only. The approval id and deadline travel
    /// in metadata, which the push channel turns into <c>approval</c>.
    /// </summary>
    public static OutboundNotification ToNotification(ApprovalRequest r, DateTimeOffset now) => new(
        Guid.NewGuid(),
        Category,
        OutboundPriority.Critical,
        r.ToolTitle,
        string.IsNullOrEmpty(r.Detail) ? r.Session : $"{r.Session}\n{r.Detail}",
        $"{Category}:{r.Id}",
        MessageSensitivity.Normal,
        DeliveryPolicy.EncryptedOnly,
        now,
        ExpiresAt: r.ExpiresAt,
        Metadata: new Dictionary<string, string>
        {
            ["sourceApp"] = r.App,
            [MetaApprovalId] = r.Id,
            [MetaApprovalExpires] = r.ExpiresAt.ToString("O"),
        });

    public const string MetaApprovalId = "approvalId";
    public const string MetaApprovalExpires = "approvalExpiresAt";

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string Head(string text, int lines)
    {
        var l = text.Split('\n');
        return string.Join('\n', l.Take(lines)) + (l.Length > lines ? $"\n… ({l.Length - lines}줄 더)" : "");
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
