namespace LocalOpsBot.Core.Notifications;

/// <summary>One forwarded Windows toast after filtering and masking.</summary>
public sealed record ToastItem(string SourceApp, string Title, string? Body, DateTimeOffset At);

/// <summary>
/// What to send for one app: a single toast, or a bundle of the toasts that arrived during the
/// app's window. <see cref="Count"/> is how many toasts it stands for; <see cref="Items"/> keeps
/// only the newest <see cref="ToastBundler.MaxItemsPerBundle"/>.
/// </summary>
public sealed record ToastBatch(string SourceApp, IReadOnlyList<ToastItem> Items, int Count);

/// <summary>
/// Per-app bundling and a rolling daily budget for forwarded toasts. Alerts go through
/// <c>AlertPolicy</c>; toasts did not have any limit, which a "forward almost everything" default
/// (DECISIONS M-19) cannot afford — a chatty messenger would burn the push server's daily quota
/// (ntfy.sh anonymous: 250 messages a day).
/// <list type="bullet">
/// <item>The first toast from an app goes out at once and opens a window of <c>bundleWindow</c>.</item>
/// <item>Toasts from that app during the window are held; when it ends they go out as one bundle and
/// a new window opens, so a sustained stream costs one push per window per app.</item>
/// <item>At most <c>maxPerDay</c> pushes in any 24 hours; beyond that toasts are dropped and counted
/// (<see cref="DroppedOverBudget"/>).</item>
/// </list>
/// Not thread-safe by itself; the caller serializes <see cref="Add"/> and <see cref="Flush"/>.
/// </summary>
public sealed class ToastBundler
{
    public const int MaxItemsPerBundle = 5;

    private sealed class Window
    {
        public DateTimeOffset End;
        public readonly List<ToastItem> Pending = new();
        public int PendingCount;
    }

    private readonly TimeSpan _window;
    private readonly int _maxPerDay;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _sent = new();

    public ToastBundler(TimeSpan bundleWindow, int maxPerDay, TimeProvider time)
    {
        if (bundleWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bundleWindow));
        if (maxPerDay < 1) throw new ArgumentOutOfRangeException(nameof(maxPerDay));
        _window = bundleWindow;
        _maxPerDay = maxPerDay;
        _time = time;
    }

    /// <summary>Toasts dropped because the daily budget was used up (since start).</summary>
    public int DroppedOverBudget { get; private set; }

    /// <summary>Whether any app has held toasts waiting for its window to end.</summary>
    public bool HasPending => _windows.Values.Any(w => w.PendingCount > 0);

    /// <summary>Returns the batch to send now, or <c>null</c> when the toast was held or dropped.</summary>
    public ToastBatch? Add(ToastItem item)
    {
        var now = _time.GetUtcNow();
        if (_windows.TryGetValue(item.SourceApp, out var w) && now < w.End)
        {
            w.PendingCount++;
            w.Pending.Add(item);
            if (w.Pending.Count > MaxItemsPerBundle) w.Pending.RemoveAt(0);
            return null;
        }

        // The window ended but Flush() hasn't run yet: its held toasts go out together with this one
        // instead of being dropped when the window is replaced.
        var held = w is { PendingCount: > 0 } ? w : null;
        var items = held is null ? new List<ToastItem>() : held.Pending.ToList();
        items.Add(item);
        if (items.Count > MaxItemsPerBundle) items.RemoveRange(0, items.Count - MaxItemsPerBundle);
        var count = (held?.PendingCount ?? 0) + 1;

        _windows[item.SourceApp] = new Window { End = now + _window };
        if (!TryTakeBudget(now, count)) return null;
        return new ToastBatch(item.SourceApp, items, count);
    }

    /// <summary>Bundles of every app whose window has ended with toasts held. Call about once a second.</summary>
    public IReadOnlyList<ToastBatch> Flush()
    {
        var now = _time.GetUtcNow();
        List<ToastBatch>? due = null;
        foreach (var (app, w) in _windows.ToList())
        {
            if (now < w.End) continue;
            if (w.PendingCount == 0)
            {
                _windows.Remove(app);
                continue;
            }

            if (TryTakeBudget(now, w.PendingCount))
                (due ??= new()).Add(new ToastBatch(app, w.Pending.ToList(), w.PendingCount));
            _windows[app] = new Window { End = now + _window };
        }
        return (IReadOnlyList<ToastBatch>?)due ?? Array.Empty<ToastBatch>();
    }

    private bool TryTakeBudget(DateTimeOffset now, int toasts)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromDays(1)) _sent.Dequeue();
        if (_sent.Count >= _maxPerDay)
        {
            DroppedOverBudget += toasts;
            return false;
        }
        _sent.Enqueue(now);
        return true;
    }
}
