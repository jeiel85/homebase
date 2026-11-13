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
/// windows every 2 s and Claude sessions every 10 s, and stops on <c>watch off</c>.
/// </summary>
internal sealed class MobileBridgeClient : IDisposable
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClaudeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    private readonly bool _claudeSessions;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ClaudeSessionReader _claude = new(ClaudeSessionReader.DefaultProjectsDir);
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _reportCts;
    private IReadOnlyList<ClaudeSession> _claudeCache = Array.Empty<ClaudeSession>();
    private DateTime _claudeReadAt;

    public MobileBridgeClient(bool claudeSessions)
    {
        _claudeSessions = claudeSessions;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>True between the Agent's <c>watch on</c> and <c>watch off</c> (or a lost pipe): a phone is viewing this PC.</summary>
    public bool IsWatching { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="IsWatching"/> changes.</summary>
    public event Action<bool>? WatchingChanged;

    private void SetWatching(bool on)
    {
        if (IsWatching == on) return;
        IsWatching = on;
        WatchingChanged?.Invoke(on);
    }

    /// <summary>Sends a request and waits for the Agent's response (10 s timeout).</summary>
    public async Task<JsonObject> RequestAsync(string op, object? payload = null, CancellationToken ct = default)
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using (timeout.Token.Register(() => tcs.TrySetCanceled()))
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
            try
            {
                var pipe = new NamedPipeClientStream(".", MobileBridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, ct);
                _pipe = pipe;
                attempt = 0;
                await ReadLoopAsync(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Agent not running / restarting, or the feature is off. Retry quietly with backoff.
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

            try { await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await MobileBridge.ReadFrameAsync(pipe, ct);
            if (msg is null) return;
            switch ((string?)msg["op"])
            {
                case MobileBridge.OpWatch:
                    if ((bool?)msg["payload"]?["on"] == true)
                    {
                        StartReporting(pipe);
                        SetWatching(true);
                    }
                    else
                    {
                        StopReporting();
                        SetWatching(false);
                    }
                    break;
                case MobileBridge.OpResponse when msg["id"] is JsonValue idNode && _pending.TryRemove((string)idNode!, out var tcs):
                    tcs.TrySetResult(msg);
                    break;
            }
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
                    var report = new SessionReport(WindowEnumerator.Enumerate(), ReadClaude());
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

    private IReadOnlyList<ClaudeSession> ReadClaude()
    {
        if (!_claudeSessions) return Array.Empty<ClaudeSession>();
        var now = DateTime.UtcNow;
        if (now - _claudeReadAt >= ClaudeInterval)
        {
            _claudeCache = _claude.ReadActive(now);
            _claudeReadAt = now;
        }
        return _claudeCache;
    }

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
