using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Tray side of <c>\\.\pipe\Homebase.MobileBridge</c>. Stays connected while the Agent runs, but
/// collects nothing until the Agent says a phone is watching (<c>watch on</c>); then it reports
/// windows every 2 s and AI coding transcripts (Claude Code, Codex) every 10 s, and stops on <c>watch off</c>.
/// While connected it also watches the AI sessions' live state every 3 s — Claude Code's small
/// status files and the Codex rollouts' appended lines — and asks the Agent to push an alert when a
/// session starts waiting for the user or finishes a long turn while the user is away (M-46, M-45).
/// <c>mobile:claudeSessions</c> turns all of this off (DECISIONS M-9, M-26).
/// </summary>
internal sealed class MobileBridgeClient : IDisposable
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClaudeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AgentInterval = TimeSpan.FromSeconds(3);

    /// <summary>No keyboard or mouse input for this long: the user is not at the PC to see the session.</summary>
    internal static readonly TimeSpan AwayAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan HealthyConnection = TimeSpan.FromSeconds(30);

    private readonly bool _claudeSessions;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ClaudeSessionReader _claude = new(ClaudeSessionReader.DefaultProjectsDir);
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _reportCts;
    private readonly CodexSessionReader _codex = new(CodexSessionReader.DefaultHome);
    private readonly ClaudeStatusReader _status = new(ClaudeStatusReader.DefaultDir);
    private readonly AgentAlertDetector _detector = new();
    // Written by the session watcher, read by the report loop: whole lists swapped, never mutated.
    private volatile IReadOnlyList<ClaudeSession> _claudeCache = Array.Empty<ClaudeSession>();
    private volatile IReadOnlyList<WorkItem> _codexCache = Array.Empty<WorkItem>();
    private IReadOnlyList<ClaudeSession> _transcripts = Array.Empty<ClaudeSession>();
    private DateTime _transcriptsAt;
    private bool _watchErrorLogged;
    private readonly HashSet<string> _formatLogged = new(StringComparer.Ordinal);

    public MobileBridgeClient(bool claudeSessions)
    {
        _claudeSessions = claudeSessions;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// True while another device (a phone or a linked PC) is viewing this PC: the Agent's <c>watch</c>
    /// with <c>remote</c> (M-46), or plain <c>on</c> from an Agent older than v0.34.0. This PC's own
    /// dashboard also makes the Agent collect, but does not count.
    /// </summary>
    public bool IsWatching { get; private set; }

    /// <summary>A notification from another device to show here (DECISIONS M-28). Background thread.</summary>
    public event Action<TrayToast>? ToastRequested;

    /// <summary>The group's aliases (M-51): sent on connect and whenever they change. Background thread.</summary>
    public event Action<IReadOnlyList<DeviceAlias>>? AliasesReceived;

    /// <summary>Text another device sent, for this PC's clipboard (M-62). Background thread.</summary>
    public event Action<TrayClip>? ClipReceived;

    /// <summary>The last table received, for a listener that subscribes after the first push arrived.</summary>
    public IReadOnlyList<DeviceAlias>? LastAliases { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="IsWatching"/> changes.</summary>
    public event Action<bool>? WatchingChanged;

    private void SetWatching(bool on)
    {
        if (IsWatching == on) return;
        IsWatching = on;
        WatchingChanged?.Invoke(on);
    }

    /// <summary>
    /// The title the work tab shows for Claude Code session <paramref name="sessionId"/>, from the last
    /// pass of the session watcher; null when it has not seen that session.
    /// </summary>
    public string? SessionTitle(string sessionId) =>
        _claudeCache.FirstOrDefault(c => string.Equals(c.Agent?.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))?.Title;

    /// <summary>Sends a message that expects no response.</summary>
    public async Task SendPushAsync(string op, object? payload, CancellationToken ct = default)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
            throw new IOException("Homebase Agent is not reachable (mobile bridge not connected).");
        await SendAsync(pipe, MobileBridge.Message(op, payload), ct);
    }

    /// <summary>Sends a request and waits for the Agent's response (10 s timeout unless given).</summary>
    public async Task<JsonObject> RequestAsync(string op, object? payload = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
            throw new IOException("Homebase Agent is not reachable (mobile bridge not connected).");

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(pipe, MobileBridge.Message(op, payload, id), ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            await using (limit.Token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            DateTime? connectedAt = null;
            try
            {
                var pipe = new NamedPipeClientStream(".", MobileBridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, ct);
                _pipe = pipe;
                connectedAt = DateTime.UtcNow;
                // Tells the Agent this tray puts received clipboard text on the clipboard (M-62). An older
                // Agent answers nothing to a push it doesn't know, and the connection stays.
                await SendAsync(pipe, MobileBridge.Message(MobileBridge.OpClipReady), ct);
                using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watcher = Task.Run(() => WatchSessionsAsync(pipe, watchCts.Token));
                try { await ReadLoopAsync(pipe, ct); }
                finally
                {
                    watchCts.Cancel();
                    await watcher;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Agent not running / restarting, or the feature is off. Retry quietly with backoff.
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Anything unforeseen: drop this connection and reconnect with backoff rather than let
                // the bridge loop end for the rest of the session.
            }
            finally
            {
                StopReporting();
                // No Agent, no watcher we can vouch for — the tray indicator must not stay lit.
                SetWatching(false);
                _pipe?.Dispose();
                _pipe = null;
                foreach (var p in _pending.Values) p.TrySetException(new IOException("Mobile bridge disconnected."));
            }

            // A connection that held for a while was healthy: start the backoff over. One that drops
            // right away (an Agent that rejects us, a frame we can't read) keeps backing off instead of
            // reconnecting every second.
            if (connectedAt is { } at && DateTime.UtcNow - at >= HealthyConnection) attempt = 0;

            try { await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonObject? msg;
            try
            {
                msg = await MobileBridge.ReadFrameAsync(pipe, ct);
            }
            catch (JsonException)
            {
                // The whole frame was read but isn't JSON: the stream is still in step, so skip it.
                continue;
            }
            if (msg is null) return;
            try
            {
                Dispatch(pipe, msg);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
            {
                // A message of an unexpected shape (a newer or older Agent): ignore it, keep the bridge.
            }
        }
    }

    /// <summary>
    /// Handles one Agent message. A response — including an error reply such as <c>ok:false,
    /// error:"unsupported"</c> for an op the Agent doesn't know — completes its request as is; the
    /// caller checks <c>ok</c>. Unknown ops are ignored.
    /// </summary>
    private void Dispatch(NamedPipeClientStream pipe, JsonObject msg)
    {
        switch ((string?)msg["op"])
        {
            case MobileBridge.OpWatch:
                var on = (bool?)msg["payload"]?["on"] == true;
                if (on) StartReporting(pipe);
                else StopReporting();
                SetWatching(on && ((bool?)msg["payload"]?["remote"] ?? true));
                break;
            case MobileBridge.OpToast:
                if (msg["payload"]?.Deserialize<TrayToast>(MobileProtocol.Json) is { } toast) ToastRequested?.Invoke(toast);
                break;
            case MobileBridge.OpClipSet:
                if (msg["payload"]?.Deserialize<TrayClip>(MobileProtocol.Json) is { Text.Length: > 0 } clip) ClipReceived?.Invoke(clip);
                break;
            case MobileBridge.OpAliases:
                try
                {
                    if (msg["payload"]?.Deserialize<AliasListPayload>(MobileProtocol.Json) is { Aliases: { } aliases })
                    {
                        LastAliases = aliases;
                        AliasesReceived?.Invoke(aliases);
                    }
                }
                catch (JsonException) { /* a malformed table keeps the one shown */ }
                break;
            case MobileBridge.OpResponse when msg["id"] is JsonValue idNode && _pending.TryRemove((string)idNode!, out var tcs):
                tcs.TrySetResult(msg);
                break;
        }
    }

    private void StartReporting(NamedPipeClientStream pipe)
    {
        if (_reportCts is not null) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _reportCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var report = new SessionReport(WindowEnumerator.Enumerate(), _claudeCache, _codexCache);
                    await SendAsync(pipe, MobileBridge.Message(MobileBridge.OpSessionReport, report), cts.Token);
                    await Task.Delay(ReportInterval, cts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        });
    }

    private void StopReporting()
    {
        _reportCts?.Cancel();
        _reportCts?.Dispose();
        _reportCts = null;
    }

    /// <summary>
    /// The AI sessions every <see cref="AgentInterval"/>: Claude Code's status files and the Codex
    /// rollouts always (both cheap: small files, appended bytes), the Claude transcripts only while a
    /// phone watches (M-13). Feeds the report caches and turns state changes into alerts.
    /// </summary>
    private async Task WatchSessionsAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        if (!_claudeSessions) return;
        _transcriptsAt = DateTime.MinValue; // a new connection reads the transcripts at once
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<AgentAlert> alerts = Array.Empty<AgentAlert>();
                try { alerts = ReadSessions(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a file in use: next pass */ }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A format this build does not expect must not end the watcher for the session.
                    if (!_watchErrorLogged) TrayLog.Warn("agent-sessions", "Reading the AI sessions failed; retrying every pass.", ex);
                    _watchErrorLogged = true;
                }
                if (alerts.Count > 0 && UserIdle.Duration() >= AwayAfter)
                {
                    foreach (var alert in alerts)
                        await SendAsync(pipe, MobileBridge.Message(MobileBridge.OpAgentAlert, alert), ct);
                }
                await Task.Delay(AgentInterval, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private IReadOnlyList<AgentAlert> ReadSessions()
    {
        var now = DateTime.UtcNow;
        var live = _status.Read();
        var codex = _codex.ReadActive(now);
        LogFormatIssue(_status.FormatIssue);
        LogFormatIssue(_codex.FormatIssue);
        if (IsWatching && now - _transcriptsAt >= ClaudeInterval)
        {
            _transcripts = _claude.ReadTranscripts(now);
            _transcriptsAt = now;
        }
        var claude = ClaudeSessionReader.Merge(_transcripts, live);
        _claudeCache = claude;
        _codexCache = codex;

        var titles = claude.Where(c => c.Agent?.SessionId is not null)
            .GroupBy(c => c.Agent!.SessionId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        var states = live
            .Select(l => new AgentState(
                "claude:" + l.SessionId,
                ClaudeSession.AppName,
                titles.TryGetValue(l.SessionId, out var t) ? t : l.Name ?? FolderName(l.Cwd) ?? "Claude Code",
                l.Cwd,
                l.Status,
                l.WaitingFor,
                l.StatusSince))
            .Concat(codex
                .Where(c => c.Agent?.SessionId is not null)
                .Select(c => new AgentState("codex:" + c.Agent!.SessionId, c.App, c.Title, c.Detail,
                    c.Agent.Status, null, c.Agent.StatusSince)));
        return _detector.Update(states, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A session file this build reads without the fields it needs (#61): said once per reader, so a
    /// Claude Code or Codex update that changed the format explains empty cards in tray.log.
    /// </summary>
    private void LogFormatIssue(string? issue)
    {
        if (issue is null) return;
        var reader = issue.StartsWith("Codex", StringComparison.Ordinal) ? "codex" : "claude";
        if (!_formatLogged.Add(reader)) return;
        TrayLog.Warn("agent-sessions", $"Session format not recognized; cards and alerts may miss it: {issue}");
    }

    private static string? FolderName(string? cwd) =>
        string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : null;

    private async Task SendAsync(Stream pipe, JsonObject message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await MobileBridge.WriteFrameAsync(pipe, message, ct); }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        StopReporting();
        _pipe?.Dispose();
    }
}
