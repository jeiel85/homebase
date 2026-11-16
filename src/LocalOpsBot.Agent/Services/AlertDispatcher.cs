using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Protocol.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Sends the alerts the policy lets through and records each in <c>alert_log</c>.
/// <para>An alert that no channel delivered because of a passing failure (network or DNS down,
/// timeout, 5xx, rate limit, phone offline) is re-sent in the background with backoff, for at most
/// <see cref="MaxRetryAge"/> (#2): the alerts raised while the network is unstable are often about
/// that very problem. Only one re-send per dedup key waits at a time, and a later delivery of the same
/// key cancels it, so the same alert is not delivered twice. When a re-send succeeds, its
/// <c>alert_log</c> row turns from <c>Failed</c> to <c>Sent</c>. Re-sends are kept in memory only;
/// a restart forgets them.</para>
/// </summary>
public sealed class AlertDispatcher : BackgroundService, IAlertDispatcher
{
    /// <summary>
    /// Wait before re-send n (index n-1), from the previous attempt. The last one runs about 28 minutes
    /// after the first failure; after it the alert is given up.
    /// </summary>
    internal static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10),
    };

    /// <summary>An alert whose first failure is older than this is not re-sent: it would come too late to act on.</summary>
    internal static readonly TimeSpan MaxRetryAge = TimeSpan.FromMinutes(30);

    /// <summary>Most alerts waiting for a re-send; in a long outage the oldest gives way.</summary>
    internal const int MaxPendingRetries = 50;

    private static readonly TimeSpan RetryTick = TimeSpan.FromSeconds(10);

    private sealed class PendingRetry(AlertEvent alert, OutboundNotification notification, DateTimeOffset firstFailedAt)
    {
        public AlertEvent Alert { get; } = alert;
        public OutboundNotification Notification { get; } = notification;
        public DateTimeOffset FirstFailedAt { get; } = firstFailedAt;
        public int Attempts { get; set; }
        public DateTimeOffset NextAt { get; set; }
    }

    private readonly IAlertPolicy _policy;
    private readonly IAlertStore _store;
    private readonly IOutboundRouter _outbound;
    private readonly ILogger<AlertDispatcher> _logger;
    private readonly TimeProvider _time;
    private readonly object _retryGate = new();
    private readonly List<PendingRetry> _retries = new();

    public AlertDispatcher(
        IAlertPolicy policy,
        IAlertStore store,
        IOutboundRouter outbound,
        ILogger<AlertDispatcher> logger,
        TimeProvider? time = null)
    {
        _policy = policy;
        _store = store;
        _outbound = outbound;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Alerts waiting for a re-send.</summary>
    internal int PendingRetries
    {
        get { lock (_retryGate) return _retries.Count; }
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

            // Before the log write, so a store failure cannot cost the re-send.
            var retry = !result.Delivered && IsRetryable(result);
            if (result.Delivered) OnDelivered(alert.DedupKey);
            else if (retry) ScheduleRetry(alert, notification);

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
            else if (retry)
            {
                _logger.LogError(
                    "No outbound channel delivered alert {Title}: {Error}. Re-sending for up to {Minutes} min.",
                    alert.Title,
                    error,
                    (int)MaxRetryAge.TotalMinutes);
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_outbound is OutboundRouter { DuplicateChannelIds.Count: > 0 } router)
            _logger.LogWarning("Outbound channel(s) registered more than once; the first registration is used: {Channels}",
                string.Join(", ", router.DuplicateChannelIds));

        using var timer = new PeriodicTimer(RetryTick, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RetryDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Alert re-send pass failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Worth re-sending: no channel delivered, and at least one failed for a reason that can pass.
    /// A failure only configuration or the user can fix (not configured, unauthorized, rejected,
    /// subscription gone) is not retried.
    /// </summary>
    internal static bool IsRetryable(OutboundDeliveryResult result) =>
        !result.Delivered && result.Attempts.Any(a => !a.Success && a.FailureKind is
            OutboundFailureKind.Transient or OutboundFailureKind.RateLimited
            or OutboundFailureKind.EndpointOffline or OutboundFailureKind.Unknown);

    /// <summary>Re-sends the alerts whose wait has passed. The hosted loop calls it every 10 s; visible for tests.</summary>
    internal async Task RetryDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        List<PendingRetry> due;
        List<PendingRetry> stale;
        lock (_retryGate)
        {
            // A PC that slept through its re-sends must not deliver an hour-old alert on wake-up.
            stale = _retries.Where(r => now - r.FirstFailedAt > MaxRetryAge).ToList();
            foreach (var r in stale) _retries.Remove(r);
            due = _retries.Where(r => r.NextAt <= now).ToList();
        }
        foreach (var r in stale)
            _logger.LogError("Gave up re-sending alert {Title}: older than {Minutes} min", r.Alert.Title, (int)MaxRetryAge.TotalMinutes);

        foreach (var retry in due)
        {
            lock (_retryGate)
            {
                // Delivered under the same dedup key, or replaced by a newer alert, meanwhile.
                if (!_retries.Contains(retry)) continue;
            }

            OutboundDeliveryResult result;
            try
            {
                result = await _outbound.DeliverAsync(retry.Notification, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new OutboundDeliveryResult(false,
                    [new OutboundAttemptResult("router", false, OutboundFailureKind.Unknown, ex.Message)]);
            }

            now = _time.GetUtcNow();
            int attempt;
            var giveUp = false;
            lock (_retryGate)
            {
                attempt = ++retry.Attempts;
                if (result.Delivered)
                {
                    _retries.Remove(retry);
                }
                else
                {
                    giveUp = !IsRetryable(result)
                             || attempt >= RetryDelays.Length
                             || now + RetryDelays[attempt] - retry.FirstFailedAt > MaxRetryAge;
                    if (giveUp) _retries.Remove(retry);
                    else retry.NextAt = now + RetryDelays[attempt];
                }
            }

            if (result.Delivered)
            {
                OnDelivered(retry.Alert.DedupKey);
                _logger.LogInformation(
                    "Alert delivered on re-send {Attempt}, {Seconds}s after it first failed [{Severity}]: {Title}",
                    attempt, (int)(now - retry.FirstFailedAt).TotalSeconds, retry.Alert.Severity, retry.Alert.Title);
                try
                {
                    await _store.MarkSentAsync(retry.Alert.AlertId, now, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not mark the re-sent alert as sent: {Title}", retry.Alert.Title);
                }
            }
            else if (giveUp)
            {
                _logger.LogError("Gave up re-sending alert {Title} after {Attempts} attempt(s): {Error}",
                    retry.Alert.Title, attempt, result.CombinedError);
            }
            else
            {
                _logger.LogWarning("Re-send {Attempt} of alert {Title} failed: {Error}",
                    attempt, retry.Alert.Title, result.CombinedError);
            }
        }
    }

    private void ScheduleRetry(AlertEvent alert, OutboundNotification notification)
    {
        var now = _time.GetUtcNow();
        lock (_retryGate)
        {
            // One waiting re-send per dedup key: the newer alert replaces the older one.
            if (!string.IsNullOrEmpty(alert.DedupKey))
                _retries.RemoveAll(r => r.Alert.DedupKey == alert.DedupKey);
            if (_retries.Count >= MaxPendingRetries)
            {
                var oldest = _retries.MinBy(r => r.FirstFailedAt)!;
                _retries.Remove(oldest);
                _logger.LogWarning("Too many alerts waiting for a re-send; dropped the oldest: {Title}", oldest.Alert.Title);
            }
            _retries.Add(new PendingRetry(alert, notification, now) { NextAt = now + RetryDelays[0] });
        }
    }

    /// <summary>
    /// A delivery went through: a waiting re-send of the same dedup key would deliver it twice, so it
    /// is dropped; the others are due at once, since a channel works again.
    /// </summary>
    private void OnDelivered(string? dedupKey)
    {
        var now = _time.GetUtcNow();
        lock (_retryGate)
        {
            if (_retries.Count == 0) return;
            if (!string.IsNullOrEmpty(dedupKey))
                _retries.RemoveAll(r => r.Alert.DedupKey == dedupKey);
            foreach (var r in _retries)
                if (r.NextAt > now) r.NextAt = now;
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
