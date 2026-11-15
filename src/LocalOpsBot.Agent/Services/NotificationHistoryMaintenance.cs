using LocalOpsBot.Core.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Keeps the notification center within its limits (DECISIONS M-32): relayed notifications for
/// 30 days and at most 500, the alert log for 30 days. Runs shortly after start, then once a day.
/// </summary>
public sealed class NotificationHistoryMaintenance : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly INotificationHistoryStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationHistoryMaintenance> _logger;

    public NotificationHistoryMaintenance(INotificationHistoryStore store, ILogger<NotificationHistoryMaintenance> logger, TimeProvider? time = null)
    {
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstRunDelay, _time, ct);
            while (!ct.IsCancellationRequested)
            {
                await PruneOnceAsync(ct);
                await Task.Delay(Interval, _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task PruneOnceAsync(CancellationToken ct)
    {
        try
        {
            var removed = await _store.PruneAsync(
                _time.GetUtcNow() - NotificationHistoryRules.Retention, NotificationHistoryRules.MaxDeviceEntries, ct);
            if (removed > 0) _logger.LogInformation("Notification center: removed {Count} entries past their retention", removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Try again tomorrow; the history only grows a little meanwhile.
            _logger.LogWarning(ex, "Notification center cleanup failed");
        }
    }
}
