using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Localization;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Data.Repositories;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services;

public sealed class TelegramPollingService : BackgroundService
{
    private readonly ITelegramClient _telegram;
    private readonly IChatAuthorizationPolicy _auth;
    private readonly ICommandRouter _router;
    private readonly IRuntimeStateRepository _stateRepo;
    private readonly IOptions<TelegramOptions> _options;
    private readonly ILogger<TelegramPollingService> _logger;
    private readonly ITelegramPollStatus _pollStatus;
    private long? _offset;

    private const string OffsetKey = "telegram.last_update_offset";

    public TelegramPollingService(
        ITelegramClient telegram,
        IChatAuthorizationPolicy auth,
        ICommandRouter router,
        IRuntimeStateRepository stateRepo,
        IOptions<TelegramOptions> options,
        ILogger<TelegramPollingService> logger,
        ITelegramPollStatus pollStatus)
    {
        _telegram = telegram;
        _auth = auth;
        _router = router;
        _stateRepo = stateRepo;
        _options = options;
        _logger = logger;
        _pollStatus = pollStatus;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = _options.Value;

        if (!_telegram.IsConfigured)
        {
            // Fresh install before onboarding set a token: run idle instead of crash-looping.
            // The token is read at startup, so polling begins after the service restarts.
            _logger.LogWarning(
                "Telegram polling idle: no bot token configured yet. Set it in the Homebase tray " +
                "onboarding; polling starts after the service restarts.");
            return;
        }

        _logger.LogInformation("Telegram polling started");

        var stored = await _stateRepo.GetAsync(OffsetKey, ct);
        if (long.TryParse(stored, out var parsed))
        {
            _offset = parsed;
            _logger.LogInformation("Resumed polling from offset {Offset}", _offset);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _telegram.GetUpdatesAsync(_offset, opts.PollingTimeoutSeconds, ct);
                _pollStatus.RecordSuccess();
                foreach (var update in updates)
                {
                    await ProcessUpdateAsync(update, ct);
                    _offset = update.UpdateId + 1;
                }

                if (_offset.HasValue)
                    await _stateRepo.SetAsync(OffsetKey, _offset.Value.ToString(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TelegramApiException ex) when (ex.HttpStatusCode == 401)
            {
                _pollStatus.RecordFailure();
                _logger.LogCritical(ex, "Telegram bot token is invalid (401). Polling stopped.");
                break;
            }
            catch (Exception ex)
            {
                _pollStatus.RecordFailure();
                if (IsTransient(ex, ct))
                {
                    // A DNS failure or timeout fixes itself on a later poll; a one-line warning
                    // keeps a flaky network from burying real errors under stack traces.
                    _logger.LogWarning(
                        "Telegram polling failed ({Error}), backing off {Seconds}s",
                        $"{ex.GetType().Name}: {ex.Message}", opts.PollingErrorBackoffSeconds);
                }
                else
                {
                    _logger.LogError(ex, "Telegram polling failed, backing off {Seconds}s", opts.PollingErrorBackoffSeconds);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(opts.PollingErrorBackoffSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Whether a polling failure is a network or server-side blip that the next poll is
    /// expected to get past: no route or DNS (HttpRequestException), an HttpClient timeout
    /// (cancellation while our own token is still live), or a Telegram 429/5xx answer.
    /// </summary>
    internal static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException => true,
        TelegramApiException api => api.HttpStatusCode == 429 || api.HttpStatusCode >= 500,
        OperationCanceledException => !ct.IsCancellationRequested,
        IOException => true,
        _ => false,
    };

    private async Task ProcessUpdateAsync(TelegramUpdate update, CancellationToken ct)
    {
        if (update.Message == null) return;
        var msg = update.Message;

        var cmd = BotCommandParser.Parse(msg.Text ?? string.Empty, msg.Chat.Id, msg.From?.Id, msg.Date);
        if (cmd == null) return;

        if (!_auth.IsAllowed(cmd.ChatId, cmd.UserId))
        {
            _logger.LogWarning("Unauthorized chat {ChatId} from user {UserId}", cmd.ChatId, cmd.UserId);
            if (_options.Value.RespondToUnauthorized)
                await _telegram.SendMessageAsync(cmd.ChatId, Strings.Unauthorized, null, ct);
            return;
        }

        var result = await _router.RouteAsync(cmd, ct);
        if (result.SendResponse && !string.IsNullOrWhiteSpace(result.ResponseText))
            await _telegram.SendMessageAsync(cmd.ChatId, result.ResponseText, null, ct);
    }
}
