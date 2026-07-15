using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Protocol.Messaging;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

public sealed class AlertDispatcher : IAlertDispatcher
{
    private readonly IAlertPolicy _policy;
    private readonly IAlertStore _store;
    private readonly IOutboundRouter _outbound;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        IAlertPolicy policy,
        IAlertStore store,
        IOutboundRouter outbound,
        ILogger<AlertDispatcher> logger)
    {
        _policy = policy;
        _store = store;
        _outbound = outbound;
        _logger = logger;
    }

    public async Task DispatchAsync(
        AlertEvent alert,
        CancellationToken ct)
    {
        AlertDecision decision;
        try
        {
            decision = await _policy.ShouldSendAsync(alert, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Alert policy check failed for {Kind}: {Title}",
                alert.Kind,
                alert.Title);
            return;
        }

        if (!decision.Send)
        {
            _logger.LogInformation(
                "Alert suppressed ({Reason}): {Title}",
                decision.DropReason,
                alert.Title);
            return;
        }

        var notification = Map(alert);

        try
        {
            var result = await _outbound.DeliverAsync(
                notification,
                ct);

            var status = result.Delivered ? "Sent" : "Failed";
            var error = result.Delivered ? null : result.CombinedError;
            var sentAt = result.Delivered
                ? DateTimeOffset.UtcNow
                : (DateTimeOffset?)null;

            await _store.InsertAsync(
                ToLog(alert, status, error, sentAt),
                ct);

            if (result.Delivered)
            {
                _logger.LogInformation(
                    "Alert delivered [{Severity}]: {Title}",
                    alert.Severity,
                    alert.Title);
            }
            else
            {
                _logger.LogError(
                    "No outbound channel delivered alert {Title}: {Error}",
                    alert.Title,
                    error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to deliver alert: {Title}",
                alert.Title);

            try
            {
                await _store.InsertAsync(
                    ToLog(alert, "Failed", ex.Message, null),
                    ct);
            }
            catch (Exception storeEx)
            {
                _logger.LogError(
                    storeEx,
                    "Also failed to record alert failure");
            }
        }
    }

    private static OutboundNotification Map(AlertEvent alert)
        => new(
            Guid.TryParse(alert.AlertId, out var id)
                ? id
                : Guid.NewGuid(),
            alert.Kind,
            alert.Severity switch
            {
                AlertSeverity.Critical =>
                    OutboundPriority.Critical,
                AlertSeverity.Warning =>
                    OutboundPriority.Warning,
                AlertSeverity.Recovery =>
                    OutboundPriority.Recovery,
                _ => OutboundPriority.Info
            },
            alert.Title,
            alert.Body,
            alert.DedupKey,
            MessageSensitivity.Normal,
            alert.Severity == AlertSeverity.Critical
                ? DeliveryPolicy.Both
                : DeliveryPolicy.TelegramFallback,
            alert.CreatedAt,
            Metadata: new Dictionary<string, string>
            {
                ["source"] = alert.Source
            });

    private static AlertLogItem ToLog(
        AlertEvent alert,
        string status,
        string? error,
        DateTimeOffset? sentAt)
        => new(
            null,
            alert.AlertId,
            alert.Kind,
            alert.Severity.ToString(),
            alert.Title,
            alert.Body,
            alert.DedupKey,
            alert.Source,
            status,
            error,
            alert.CreatedAt,
            sentAt);
}
