using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Central outbound path for automatic alerts. Runs every alert through the
/// alert policy (mute / dedup / rate-limit) before sending it to Telegram, and
/// records sent/failed alerts in alert_log (which is also the dedup source).
/// Dropped alerts are intentionally NOT persisted so they never seed the dedup
/// window. Background monitors depend on <see cref="IAlertDispatcher"/> only.
/// </summary>
public sealed class AlertDispatcher : IAlertDispatcher
{
    private readonly IAlertPolicy _policy;
    private readonly IAlertStore _store;
    private readonly ITelegramClient _telegram;
    private readonly IOptions<TelegramOptions> _telegramOptions;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        IAlertPolicy policy,
        IAlertStore store,
        ITelegramClient telegram,
        IOptions<TelegramOptions> telegramOptions,
        ILogger<AlertDispatcher> logger)
    {
        _policy = policy;
        _store = store;
        _telegram = telegram;
        _telegramOptions = telegramOptions;
        _logger = logger;
    }

    public async Task DispatchAsync(AlertEvent alert, CancellationToken ct)
    {
        AlertDecision decision;
        try
        {
            decision = await _policy.ShouldSendAsync(alert, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alert policy check failed for {Kind}: {Title}", alert.Kind, alert.Title);
            return;
        }

        if (!decision.Send)
        {
            _logger.LogInformation("Alert suppressed ({Reason}): {Title}", decision.DropReason, alert.Title);
            return;
        }

        var chatId = _telegramOptions.Value.AllowedChatIds.FirstOrDefault();
        if (chatId == 0)
        {
            _logger.LogWarning("Alert not sent (no allowed chat configured): {Title}", alert.Title);
            return;
        }

        var text = Format(alert);
        try
        {
            await _telegram.SendMessageAsync(chatId, text, new TelegramSendOptions(), ct);
            await _store.InsertAsync(ToLog(alert, "Sent", null, DateTimeOffset.UtcNow), ct);
            _logger.LogInformation("Alert sent [{Severity}]: {Title}", alert.Severity, alert.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert: {Title}", alert.Title);
            try { await _store.InsertAsync(ToLog(alert, "Failed", ex.Message, null), ct); }
            catch (Exception storeEx) { _logger.LogError(storeEx, "Also failed to record alert failure"); }
        }
    }

    private static AlertLogItem ToLog(AlertEvent a, string status, string? error, DateTimeOffset? sentAt)
        => new(null, a.AlertId, a.Kind, a.Severity.ToString(), a.Title,
               a.Body, a.DedupKey, a.Source, status, error, a.CreatedAt, sentAt);

    private static string Format(AlertEvent a)
    {
        var icon = a.Severity switch
        {
            AlertSeverity.Critical => "\U0001f534", // red circle
            AlertSeverity.Warning => "\U0001f7e1",  // yellow circle
            AlertSeverity.Recovery => "\U0001f7e2", // green circle
            _ => "ℹ️",                     // info
        };
        var body = string.IsNullOrWhiteSpace(a.Body) ? string.Empty : $"\n{HtmlEscape(a.Body)}";
        return $"{icon} <b>{HtmlEscape(a.Title)}</b>{body}";
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
