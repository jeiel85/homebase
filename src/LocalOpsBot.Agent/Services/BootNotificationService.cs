using System.Net.NetworkInformation;
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
/// capped exponential backoff (5s, 10s, 20s, 40s, then 60s) for about 16 minutes. If the network
/// comes up later than that (a laptop booted with no Wi-Fi in range), a network change still
/// starts a short extra round, up to <see cref="MaxNetworkRounds"/> times within
/// <see cref="NetworkWaitWindow"/>; after that it gives up.
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
    private readonly Func<TimeSpan, CancellationToken, Task<bool>> _networkChanged;

    private const string LastNotifyKey = "boot.last_notification_at";
    private const int DedupMinutes = 10;

    public const int MaxAttempts = 20;
    /// <summary>Attempts in each extra round that a network change starts after the first round failed.</summary>
    public const int NetworkRoundAttempts = 6;
    /// <summary>Extra rounds network changes may start before the service gives up.</summary>
    public const int MaxNetworkRounds = 5;
    /// <summary>How long after the first round fails a network change may still start a round.</summary>
    public static readonly TimeSpan NetworkWaitWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    /// <param name="delay">Wait between attempts; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Tests pass a no-op to run the retry loop instantly.</param>
    /// <param name="networkChanged">Waits up to the given time for a network change and returns whether
    /// one happened; defaults to <see cref="WaitForNetworkChangeAsync"/>. Tests pass a stub.</param>
    public BootNotificationService(
        IOutboundRouter router,
        IHostInfoProvider hostInfo,
        IRuntimeStateRepository stateRepo,
        ILogger<BootNotificationService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan, CancellationToken, Task<bool>>? networkChanged = null)
    {
        _router = router;
        _hostInfo = hostInfo;
        _stateRepo = stateRepo;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _networkChanged = networkChanged ?? WaitForNetworkChangeAsync;
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

        var error = await RunRoundAsync(MaxAttempts, ct);
        if (error is null)
            return;

        // The fixed schedule above covers a slow boot, not a network that only appears later.
        // Keep listening for a network change for a bounded time, and run a short round on each.
        var deadline = DateTimeOffset.UtcNow + NetworkWaitWindow;
        for (var round = 1; round <= MaxNetworkRounds; round++)
        {
            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
                break;

            _logger.LogWarning(
                "Boot notification not sent yet ({Error}); will retry when the network changes (for up to {Minutes} min)",
                error, (int)left.TotalMinutes);
            if (!await _networkChanged(left, ct))
                break;

            _logger.LogInformation("Network changed; retrying boot notification (round {Round}/{Max})",
                round, MaxNetworkRounds);
            error = await RunRoundAsync(NetworkRoundAttempts, ct);
            if (error is null)
                return;
        }

        _logger.LogError("Failed to send boot notification ({Error}); giving up", error);
    }

    /// <summary>
    /// Up to <paramref name="maxAttempts"/> attempts with <see cref="RetryDelay"/> between them.
    /// Returns null when nothing is left to do (sent, or no channel is configured), otherwise the
    /// last attempt's error.
    /// </summary>
    private async Task<string?> RunRoundAsync(int maxAttempts, CancellationToken ct)
    {
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
                    return null;
                }

                if (result.Attempts.All(a => a.FailureKind == OutboundFailureKind.NotConfigured))
                {
                    _logger.LogWarning("Boot notification skipped: no notification channel is configured");
                    return null;
                }
                error = result.CombinedError ?? "not delivered";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message;
            }

            if (attempt >= maxAttempts)
                return error;

            var wait = RetryDelay(attempt);
            _logger.LogWarning("Boot notification attempt {Attempt}/{Max} failed ({Error}); retrying in {Delay}s",
                attempt, maxAttempts, error, (int)wait.TotalSeconds);
            await _delay(wait, ct);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the network to become available or an address to
    /// change; false when the time ran out. The event handlers are removed again before returning.
    /// </summary>
    internal static async Task<bool> WaitForNetworkChangeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkAvailabilityChangedEventHandler onAvailability = (_, e) =>
        {
            if (e.IsAvailable)
                changed.TrySetResult();
        };
        NetworkAddressChangedEventHandler onAddress = (_, _) => changed.TrySetResult();

        NetworkChange.NetworkAvailabilityChanged += onAvailability;
        NetworkChange.NetworkAddressChanged += onAddress;
        try
        {
            await changed.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= onAvailability;
            NetworkChange.NetworkAddressChanged -= onAddress;
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
