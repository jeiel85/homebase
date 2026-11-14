using System.Threading.Channels;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Builds live snapshots — but only while at least one phone is watching. The first watcher
/// starts the sampler (and its ETW session) and tells the Tray to start reporting windows; the
/// last one leaving stops all of it, so an idle Homebase costs nothing extra (DECISIONS M-5).
/// </summary>
public sealed class LiveSnapshotHub : IDisposable
{
    /// <summary>A Tray report older than this means the user session is gone (logged off, Tray closed).</summary>
    public static readonly TimeSpan SessionStaleAfter = TimeSpan.FromSeconds(15);

    private readonly IResourceSampler _sampler;
    private readonly ILogger<LiveSnapshotHub> _logger;
    private readonly TimeProvider _time;
    private readonly int _intervalMs;
    private readonly object _gate = new();
    private readonly List<Channel<LiveSnapshot>> _subscribers = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private SessionReport? _session;
    private DateTimeOffset _sessionAt;

    public LiveSnapshotHub(IResourceSampler sampler, IOptions<MobileOptions> options, TimeProvider time, ILogger<LiveSnapshotHub> logger)
    {
        _sampler = sampler;
        _time = time;
        _logger = logger;
        _intervalMs = options.Value.ClampedIntervalMs;
    }

    /// <summary>Raised with <c>true</c> when the first watcher arrives and <c>false</c> when the last leaves.</summary>
    public event Action<bool>? WatchingChanged;

    public int Watchers { get { lock (_gate) return _subscribers.Count; } }

    public bool IsWatching => Watchers > 0;

    public void UpdateSession(SessionReport report)
    {
        lock (_gate)
        {
            _session = report;
            _sessionAt = _time.GetUtcNow();
        }
    }

    /// <summary>Registers a watcher. Dispose the returned subscription to leave.</summary>
    public Subscription Subscribe()
    {
        // Capacity 1 + DropOldest: a slow phone always gets the latest snapshot, never a backlog.
        var channel = Channel.CreateBounded<LiveSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        bool first;
        lock (_gate)
        {
            _subscribers.Add(channel);
            first = _subscribers.Count == 1;
            if (first)
            {
                _sampler.Start();
                _sampler.Sample(); // prime the deltas so the first pushed tick has real rates
                // Capture this loop's own token: the lambda may run after Unsubscribe nulled
                // _loopCts or a new watcher replaced it, and each loop must keep its own lifetime.
                var cts = new CancellationTokenSource();
                _loopCts = cts;
                _loop = Task.Run(() => RunAsync(cts.Token));
            }
        }
        if (first)
        {
            _logger.LogInformation("Mobile live view started.");
            WatchingChanged?.Invoke(true);
        }
        return new Subscription(this, channel);
    }

    private void Unsubscribe(Channel<LiveSnapshot> channel)
    {
        bool last;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            if (!_subscribers.Remove(channel)) return;
            channel.Writer.TryComplete();
            last = _subscribers.Count == 0;
            cts = last ? _loopCts : null;
            loop = last ? _loop : null;
            if (last) { _loopCts = null; _loop = null; }
        }
        if (!last) return;

        cts!.Cancel();
        try { loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* cancelled */ }
        cts.Dispose();
        bool stopped;
        lock (_gate)
        {
            // Another watcher may have arrived while we were stopping; it restarted the sampler.
            stopped = _subscribers.Count == 0;
            if (stopped) _sampler.Stop();
        }
        if (!stopped) return;
        _logger.LogInformation("Mobile live view stopped.");
        WatchingChanged?.Invoke(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                LiveSnapshot snapshot;
                try { snapshot = Build(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Building a live snapshot failed; skipping this tick.");
                    continue;
                }
                lock (_gate)
                {
                    foreach (var s in _subscribers) s.Writer.TryWrite(snapshot);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public LiveSnapshot Build()
    {
        var (processes, system) = _sampler.Sample();
        SessionReport? session;
        lock (_gate)
        {
            session = _time.GetUtcNow() - _sessionAt <= SessionStaleAfter ? _session : null;
        }
        var claude = session?.ClaudeSessions ?? Array.Empty<ClaudeSession>();
        IReadOnlyList<WorkItem> work = Array.Empty<WorkItem>();
        if (session is not null)
        {
            var names = new Dictionary<int, string>(processes.Count);
            foreach (var p in processes) names[p.Pid] = p.Name;
            work = WorkCatalog.Build(
                claude.Select(c => c.ToWork()).Concat(session.Agents ?? Array.Empty<WorkItem>()),
                session.Windows,
                pid => names.TryGetValue(pid, out var n) ? n : null);
        }
        return new LiveSnapshot(
            MobileProtocol.Schema,
            _time.GetUtcNow(),
            Environment.MachineName,
            system,
            session is not null,
            _sampler.NetPerProcess,
            session is null ? Array.Empty<AppEntry>() : AppAggregator.BuildApps(processes, session.Windows),
            AppAggregator.TopProcesses(processes),
            claude,
            work);
    }

    public void Dispose()
    {
        List<Channel<LiveSnapshot>> subs;
        lock (_gate) subs = _subscribers.ToList();
        foreach (var s in subs) Unsubscribe(s);
    }

    public sealed class Subscription : IDisposable
    {
        private readonly LiveSnapshotHub _hub;
        private readonly Channel<LiveSnapshot> _channel;
        private int _disposed;

        internal Subscription(LiveSnapshotHub hub, Channel<LiveSnapshot> channel)
        {
            _hub = hub;
            _channel = channel;
        }

        public ChannelReader<LiveSnapshot> Reader => _channel.Reader;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _hub.Unsubscribe(_channel);
        }
    }
}
