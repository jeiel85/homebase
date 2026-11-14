using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Localization;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Protocol.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Forwards Windows toasts relayed by the Tray (pipe <c>Homebase.NotificationPipe</c>).
/// The Tray already dropped blocked apps and masked OTPs/passwords; here each toast is masked again,
/// bundled per app and kept within a daily budget (<see cref="ToastBundler"/>), then routed with
/// <see cref="DeliveryPolicy.EncryptedOnly"/> — KDE Connect or encrypted UnifiedPush, never the
/// Telegram cloud in plaintext — unless <c>notificationForwarding:telegramFallback</c> is set
/// (DECISIONS M-19).
/// <para>Notifications from paired devices and linked hub PCs (DECISIONS M-28) take the same path with
/// category <c>device-notification</c>: they are shown on this PC through the Tray, and the push skips
/// the device that sent them and the phones that got them elsewhere. This PC's own toasts and what its
/// devices sent go to the <see cref="NotificationInbox"/> for linked PCs; what came from a hub does not.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NotificationForwardingService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BudgetWarningInterval = TimeSpan.FromHours(1);

    private readonly INotificationBridgeServer _bridgeServer;
    private readonly IOutboundRouter _outbound;
    private readonly ITextMasker _masker;
    private readonly ToastForwardingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationForwardingService> _logger;
    private readonly ToastBundler _bundler;
    private readonly NotificationInbox? _inbox;
    private readonly ITrayToaster? _toaster;
    private readonly object _gate = new();
    private int _droppedReported;
    private DateTimeOffset _lastBudgetWarning = DateTimeOffset.MinValue;

    public NotificationForwardingService(
        INotificationBridgeServer bridgeServer,
        IOutboundRouter outbound,
        ITextMasker masker,
        ToastForwardingOptions options,
        ILogger<NotificationForwardingService> logger,
        TimeProvider? time = null,
        NotificationInbox? inbox = null,
        ITrayToaster? toaster = null)
    {
        _inbox = inbox;
        _toaster = toaster;
        _bridgeServer = bridgeServer;
        _outbound = outbound;
        _masker = masker;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _bundler = new ToastBundler(
            TimeSpan.FromSeconds(Math.Max(0, options.BundleSeconds)),
            Math.Max(1, options.MaxPushesPerDay),
            _time);
    }

    public override Task StartAsync(CancellationToken ct)
    {
        _bridgeServer.NotificationReceived += OnNotificationReceived;
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        _bridgeServer.NotificationReceived -= OnNotificationReceived;
        return base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(FlushInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                IReadOnlyList<ToastBatch> due;
                lock (_gate) due = _bundler.Flush();
                foreach (var batch in due) await SendAsync(batch);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Raised for each of this PC's own Windows toasts after filtering and masking — the hub link
    /// (DECISIONS M-29) sends them on to the PCs this one is linked to.
    /// </summary>
    public event Action<ToastItem>? LocalAccepted;

    /// <summary>Category of notifications relayed from another device (DECISIONS M-28).</summary>
    public const string DeviceCategory = "device-notification";

    private void OnNotificationReceived(ToastNotificationEvent notification)
    {
        _ = AcceptAsync(notification);
    }

    /// <summary>Visible for tests: filter, mask and bundle one toast; send it if it is not held.</summary>
    internal async Task AcceptAsync(ToastNotificationEvent notification)
    {
        try
        {
            if (notification.Sensitivity == NotificationSensitivity.Blocked)
            {
                _logger.LogInformation("Dropped blocked notification from {App}", notification.SourceApp);
                return;
            }

            var item = new ToastItem(
                notification.SourceApp,
                _masker.Mask(notification.Title ?? string.Empty),
                _masker.Mask(notification.Body ?? string.Empty),
                notification.CreatedAt,
                Id: Guid.NewGuid().ToString("N"));

            _inbox?.Add(NotificationInbox.SelfOrigin, Environment.MachineName, item.SourceApp, item.Title,
                item.Body is { Length: > 0 } b ? b : null, item.At, item.Id);
            try { LocalAccepted?.Invoke(item); }
            catch (Exception ex) { _logger.LogWarning(ex, "Hub link rejected a local notification"); }
            await BundleAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward notification");
        }
    }

    /// <summary>
    /// A notification another device sent (DECISIONS M-28): masked, shown on this PC, kept for linked
    /// PCs unless it came from a hub, and pushed to this PC's other phones through the bundler.
    /// </summary>
    public async Task AcceptFromDeviceAsync(ToastOrigin origin, string app, string title, string? body, DateTimeOffset at, string? id = null)
    {
        try
        {
            if (id is not null && !FirstSighting(id)) return;
            var item = new ToastItem(app, _masker.Mask(title), body is null ? null : _masker.Mask(body), at, origin, id);
            _toaster?.Show($"{origin.Name} · {app}", ToastText(item));
            if (!origin.FromHub) _inbox?.Add(origin.DeviceId, origin.Name, app, item.Title, item.Body, at, id);
            await BundleAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to relay a notification from {Device}", origin.Name);
        }
    }

    private readonly Dictionary<string, DateTimeOffset> _seenIds = new(StringComparer.Ordinal);

    /// <summary>False when this id arrived in the last 30 minutes (the same notification by another path).</summary>
    private bool FirstSighting(string id)
    {
        var now = _time.GetUtcNow();
        lock (_seenIds)
        {
            if (_seenIds.Count > 1000)
                foreach (var old in _seenIds.Where(kv => now - kv.Value > NotificationInbox.Retention).Select(kv => kv.Key).ToList())
                    _seenIds.Remove(old);
            if (_seenIds.TryGetValue(id, out var at) && now - at <= NotificationInbox.Retention) return false;
            _seenIds[id] = now;
            return true;
        }
    }

    private static string? ToastText(ToastItem item) =>
        string.IsNullOrWhiteSpace(item.Body) ? (string.IsNullOrWhiteSpace(item.Title) ? null : item.Title)
        : string.IsNullOrWhiteSpace(item.Title) ? item.Body : item.Title + "\n" + item.Body;

    private async Task BundleAsync(ToastItem item)
    {
        ToastBatch? batch;
        lock (_gate)
        {
            batch = _bundler.Add(item);
            ReportBudgetDrops();
        }
        if (batch is not null) await SendAsync(batch);
    }

    /// <summary>Visible for tests: drain bundles whose window ended.</summary>
    internal async Task FlushAsync()
    {
        IReadOnlyList<ToastBatch> due;
        lock (_gate) due = _bundler.Flush();
        foreach (var batch in due) await SendAsync(batch);
    }

    private void ReportBudgetDrops()
    {
        var dropped = _bundler.DroppedOverBudget;
        if (dropped == _droppedReported) return;
        var now = _time.GetUtcNow();
        if (now - _lastBudgetWarning < BudgetWarningInterval) return;
        _logger.LogWarning(
            "Toast forwarding hit its budget of {Max} pushes per 24h; {Dropped} toast(s) dropped so far",
            _options.MaxPushesPerDay, dropped);
        _droppedReported = dropped;
        _lastBudgetWarning = now;
    }

    private async Task SendAsync(ToastBatch batch)
    {
        try
        {
            var outbound = ToOutbound(batch, _options.TelegramFallback, _time.GetUtcNow());
            var result = await _outbound.DeliverAsync(outbound, CancellationToken.None);
            if (result.Delivered)
                _logger.LogInformation("Forwarded {Count} notification(s) from {App}", batch.Count, batch.SourceApp);
            else
                _logger.LogWarning("No channel delivered notification(s) from {App}: {Error}",
                    batch.SourceApp, result.CombinedError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward notification");
        }
    }

    public static OutboundNotification ToOutbound(ToastBatch batch, bool telegramFallback, DateTimeOffset now)
    {
        var origin = batch.Origin;
        // A relayed notification is labelled with the device it came from: "Galaxy S24 · 카카오톡".
        var source = origin is null ? batch.SourceApp : $"{origin.Name} · {batch.SourceApp}";
        string title;
        string? body;
        if (batch.Count == 1)
        {
            var only = batch.Items[0];
            title = string.IsNullOrWhiteSpace(only.Title) ? source : $"{source}: {only.Title}";
            body = string.IsNullOrWhiteSpace(only.Body) ? null : only.Body;
        }
        else
        {
            title = Strings.ToastBundleTitle(source, batch.Count);
            var sb = new StringBuilder();
            foreach (var item in batch.Items)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(string.IsNullOrWhiteSpace(item.Body) ? item.Title
                    : string.IsNullOrWhiteSpace(item.Title) ? item.Body : $"{item.Title}: {item.Body}");
            }
            if (batch.Count > batch.Items.Count)
                sb.Append('\n').Append(Strings.ToastBundleMore(batch.Count - batch.Items.Count));
            body = sb.ToString();
        }

        var metadata = new Dictionary<string, string>
        {
            ["sourceApp"] = source,
            ["origin"] = origin is null ? "windows-toast" : DeviceCategory,
            ["count"] = batch.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (origin is not null)
        {
            metadata[PushRelay.OriginDeviceKey] = origin.DeviceId;
            if (origin.SkipPushHashes.Count > 0) metadata[PushRelay.SkipHashesKey] = string.Join(',', origin.SkipPushHashes);
        }
        return new OutboundNotification(
            Guid.NewGuid(),
            origin is null ? "windows-toast" : DeviceCategory,
            OutboundPriority.Info,
            title,
            body,
            $"toast:{source}:{now.ToUnixTimeMilliseconds()}",
            MessageSensitivity.Sensitive,
            telegramFallback ? DeliveryPolicy.LocalPreferred : DeliveryPolicy.EncryptedOnly,
            batch.Items[^1].At,
            Metadata: metadata);
    }
}

/// <summary>Agent-side toast forwarding settings (<c>notificationForwarding</c> section).</summary>
public sealed record ToastForwardingOptions(int BundleSeconds = 60, int MaxPushesPerDay = 150, bool TelegramFallback = false);
