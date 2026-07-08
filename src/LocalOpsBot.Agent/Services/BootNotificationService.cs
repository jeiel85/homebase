using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Data.Repositories;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services;

public sealed class BootNotificationService : IHostedService
{
    private readonly ITelegramClient _telegram;
    private readonly IHostInfoProvider _hostInfo;
    private readonly IRuntimeStateRepository _stateRepo;
    private readonly IOptions<TelegramOptions> _options;
    private readonly ILogger<BootNotificationService> _logger;

    private const string LastNotifyKey = "boot.last_notification_at";

    public BootNotificationService(
        ITelegramClient telegram,
        IHostInfoProvider hostInfo,
        IRuntimeStateRepository stateRepo,
        IOptions<TelegramOptions> options,
        ILogger<BootNotificationService> logger)
    {
        _telegram = telegram;
        _hostInfo = hostInfo;
        _stateRepo = stateRepo;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var opts = _options.Value;
        var targetChatId = opts.AllowedChatIds.FirstOrDefault();

        if (targetChatId == 0)
        {
            _logger.LogWarning("Boot notification skipped: no allowed chat configured");
            return;
        }

        var lastStr = await _stateRepo.GetAsync(LastNotifyKey, ct);
        if (DateTime.TryParse(lastStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastNotify))
        {
            var dedupMinutes = 10;
            if ((DateTime.UtcNow - lastNotify).TotalMinutes < dedupMinutes)
            {
                _logger.LogInformation("Boot notification skipped: last was {Last}s ago (dedup {Min}m)",
                    (int)(DateTime.UtcNow - lastNotify).TotalSeconds, dedupMinutes);
                return;
            }
        }

        try
        {
            var host = await _hostInfo.GetHostInfoAsync(ct);
            var message = BootMessageFormatter.Format(host, DateTimeOffset.UtcNow);
            await _telegram.SendMessageAsync(targetChatId, message, new TelegramSendOptions(), ct);
            await _stateRepo.SetAsync(LastNotifyKey, DateTime.UtcNow.ToString("O"), ct);
            _logger.LogInformation("Boot notification sent to chat {ChatId}", targetChatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send boot notification");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
