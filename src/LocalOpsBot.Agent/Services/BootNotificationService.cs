using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Data.Repositories;
using LocalOpsBot.Protocol.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Sends one "PC booted" message per Agent start.
/// The service starts during boot, often before the network or DNS is ready, so a single attempt
/// usually fails. Sending runs in the background (it never blocks host startup) and retries with
/// capped exponential backoff (5s, 10s, 20s, 40s, then 60s) for about 16 minutes before giving up.
/// No exception escapes <see cref="ExecuteAsync"/>: an unhandled one would stop the whole Agent.
/// Goes through <see cref="IOutboundRouter"/> like every other notification, so it reaches the phone
/// by UnifiedPush first and Telegram only as the fallback (DECISIONS M-19). When no channel is
/// configured at all it gives up at once instead of retrying.
/// </summary>
public sealed class BootNotificationService : BackgroundService
{
    private readonly IOutboundRouter _router;
    private readonly IHostInfoProvider _hostInfo;
    private readonly IRuntimeStateRepository _stateRepo;
    private readonly ILogger<BootNotificationService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private const string LastNotifyKey = "boot.last_notification_at";
    private const int DedupMinutes = 10;

    public const int MaxAttempts = 20;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    /// <param name="delay">Wait between attempts; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Tests pass a no-op to run the retry loop instantly.</param>
    public BootNotificationService(
        IOutboundRouter router,
        IHostInfoProvider hostInfo,
        IRuntimeStateRepository stateRepo,
        ILogger<BootNotificationService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _router = router;
        _hostInfo = hostInfo;
        _stateRepo = stateRepo;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Wait before the retry that follows failed attempt <paramref name="attempt"/> (1-based).</summary>
    public static TimeSpan RetryDelay(int attempt)
    {
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, attempt - 1);
        return seconds >= MaxRetryDelay.TotalSeconds ? MaxRetryDelay : TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await SendWithRetryAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Boot notification cancelled: Agent is stopping");
        }
    }

    private async Task SendWithRetryAsync(CancellationToken ct)
    {
        if (await SentRecentlyAsync(ct))
            return;

        for (var attempt = 1; ; attempt++)
        {
            string error;
            try
            {
                var host = await _hostInfo.GetHostInfoAsync(ct);
                var now = DateTimeOffset.UtcNow;
                var (title, body) = BootMessageFormatter.Compose(host, now);
                var result = await _router.DeliverAsync(
                    new OutboundNotification(
                        Guid.NewGuid(), "boot", OutboundPriority.Info, title, body,
                        $"boot:{now:yyyyMMddHHmm}", MessageSensitivity.Normal,
                        DeliveryPolicy.TelegramFallback, now),
                    ct);

                if (result.Delivered)
                {
                    var via = result.Attempts.First(a => a.Success).ChannelId;
                    _logger.LogInformation("Boot notification sent via {Channel} (attempt {Attempt})", via, attempt);
                    await RecordSentAsync(ct);
                    return;
                }

                if (result.Attempts.All(a => a.FailureKind == OutboundFailureKind.NotConfigured))
                {
                    _logger.LogWarning("Boot notification skipped: no notification channel is configured");
                    return;
                }
                error = result.CombinedError ?? "not delivered";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message;
            }

            if (attempt >= MaxAttempts)
            {
                _logger.LogError("Failed to send boot notification after {Attempts} attempts ({Error}); giving up", attempt, error);
                return;
            }

            var wait = RetryDelay(attempt);
            _logger.LogWarning("Boot notification attempt {Attempt}/{Max} failed ({Error}); retrying in {Delay}s",
                attempt, MaxAttempts, error, (int)wait.TotalSeconds);
            await _delay(wait, ct);
        }
    }

    private async Task<bool> SentRecentlyAsync(CancellationToken ct)
    {
        string? lastStr;
        try
        {
            lastStr = await _stateRepo.GetAsync(LastNotifyKey, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Without the timestamp we cannot dedup; sending a possible duplicate beats sending nothing.
            _logger.LogWarning(ex, "Boot notification: could not read last-sent time; sending without dedup");
            return false;
        }

        if (!DateTime.TryParse(lastStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastNotify))
            return false;

        var elapsed = DateTime.UtcNow - lastNotify;
        if (elapsed.TotalMinutes >= DedupMinutes)
            return false;

        _logger.LogInformation("Boot notification skipped: last was {Last}s ago (dedup {Min}m)",
            (int)elapsed.TotalSeconds, DedupMinutes);
        return true;
    }

    private async Task RecordSentAsync(CancellationToken ct)
    {
        try
        {
            await _stateRepo.SetAsync(LastNotifyKey, DateTime.UtcNow.ToString("O"), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The message already went out; retrying the send here would only duplicate it.
            _logger.LogWarning(ex, "Boot notification sent, but recording the send time failed");
        }
    }
}
