using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Localization;
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
    private readonly object _gate = new();
    private int _droppedReported;
    private DateTimeOffset _lastBudgetWarning = DateTimeOffset.MinValue;

    public NotificationForwardingService(
        INotificationBridgeServer bridgeServer,
        IOutboundRouter outbound,
        ITextMasker masker,
        ToastForwardingOptions options,
        ILogger<NotificationForwardingService> logger,
        TimeProvider? time = null)
    {
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
                notification.CreatedAt);

            ToastBatch? batch;
            lock (_gate)
            {
                batch = _bundler.Add(item);
                ReportBudgetDrops();
            }
            if (batch is not null) await SendAsync(batch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward notification");
        }
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
        string title;
        string? body;
        if (batch.Count == 1)
        {
            var only = batch.Items[0];
            title = string.IsNullOrWhiteSpace(only.Title) ? batch.SourceApp : $"{batch.SourceApp}: {only.Title}";
            body = string.IsNullOrWhiteSpace(only.Body) ? null : only.Body;
        }
        else
        {
            title = Strings.ToastBundleTitle(batch.SourceApp, batch.Count);
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

        return new OutboundNotification(
            Guid.NewGuid(),
            "windows-toast",
            OutboundPriority.Info,
            title,
            body,
            $"toast:{batch.SourceApp}:{now.ToUnixTimeMilliseconds()}",
            MessageSensitivity.Sensitive,
            telegramFallback ? DeliveryPolicy.LocalPreferred : DeliveryPolicy.EncryptedOnly,
            batch.Items[^1].At,
            Metadata: new Dictionary<string, string>
            {
                ["sourceApp"] = batch.SourceApp,
                ["origin"] = "windows-toast",
                ["count"] = batch.Count.ToString(CultureInfo.InvariantCulture),
            });
    }
}

/// <summary>Agent-side toast forwarding settings (<c>notificationForwarding</c> section).</summary>
public sealed record ToastForwardingOptions(int BundleSeconds = 60, int MaxPushesPerDay = 150, bool TelegramFallback = false);
