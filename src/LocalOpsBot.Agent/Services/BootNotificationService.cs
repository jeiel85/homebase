using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Data.Repositories;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Sends one "PC booted" message per Agent start.
/// The service starts during boot, often before the network or DNS is ready, so a single attempt
/// usually fails. Sending runs in the background (it never blocks host startup) and retries with
/// capped exponential backoff (5s, 10s, 20s, 40s, then 60s) for about 16 minutes before giving up.
/// No exception escapes <see cref="ExecuteAsync"/>: an unhandled one would stop the whole Agent.
/// </summary>
public sealed class BootNotificationService : BackgroundService
{
    private readonly ITelegramClient _telegram;
    private readonly IHostInfoProvider _hostInfo;
    private readonly IRuntimeStateRepository _stateRepo;
    private readonly IOptions<TelegramOptions> _options;
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
        ITelegramClient telegram,
        IHostInfoProvider hostInfo,
        IRuntimeStateRepository stateRepo,
        IOptions<TelegramOptions> options,
        ILogger<BootNotificationService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _telegram = telegram;
        _hostInfo = hostInfo;
        _stateRepo = stateRepo;
        _options = options;
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
        var targetChatId = _options.Value.AllowedChatIds.FirstOrDefault();
        if (targetChatId == 0)
        {
            _logger.LogWarning("Boot notification skipped: no allowed chat configured");
            return;
        }

        if (await SentRecentlyAsync(ct))
            return;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var host = await _hostInfo.GetHostInfoAsync(ct);
                var message = BootMessageFormatter.Format(host, DateTimeOffset.UtcNow);
                await _telegram.SendMessageAsync(targetChatId, message, new TelegramSendOptions(), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxAttempts)
                {
                    _logger.LogError(ex, "Failed to send boot notification after {Attempts} attempts; giving up", attempt);
                    return;
                }

                var wait = RetryDelay(attempt);
                _logger.LogWarning("Boot notification attempt {Attempt}/{Max} failed ({Error}); retrying in {Delay}s",
                    attempt, MaxAttempts, ex.Message, (int)wait.TotalSeconds);
                await _delay(wait, ct);
                continue;
            }

            _logger.LogInformation("Boot notification sent to chat {ChatId} (attempt {Attempt})", targetChatId, attempt);
            await RecordSentAsync(ct);
            return;
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
