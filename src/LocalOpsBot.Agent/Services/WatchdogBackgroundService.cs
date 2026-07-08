using System.Runtime.Versioning;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Data.Models;
using LocalOpsBot.Data.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class WatchdogBackgroundService : BackgroundService
{
    private readonly IProcessCollector _processCollector;
    private readonly IWindowsServiceCollector _serviceCollector;
    private readonly IReadOnlyList<ProcessWatchConfig> _processWatches;
    private readonly IReadOnlyList<ServiceWatchConfig> _serviceWatches;
    private readonly IAlertDispatcher _dispatcher;
    private readonly IWatchStatusRepository _watchStatus;
    private readonly AlertingOptions _alerting;
    private readonly CollectorOptions _collectors;
    private readonly ILogger<WatchdogBackgroundService> _logger;
    private readonly string _machineName = Environment.MachineName;

    public WatchdogBackgroundService(
        IProcessCollector processCollector,
        IWindowsServiceCollector serviceCollector,
        IEnumerable<ProcessWatchConfig> processWatches,
        IEnumerable<ServiceWatchConfig> serviceWatches,
        IAlertDispatcher dispatcher,
        IWatchStatusRepository watchStatus,
        AlertingOptions alerting,
        CollectorOptions collectors,
        ILogger<WatchdogBackgroundService> logger)
    {
        _processCollector = processCollector;
        _serviceCollector = serviceCollector;
        _processWatches = processWatches.ToList();
        _serviceWatches = serviceWatches.ToList();
        _dispatcher = dispatcher;
        _watchStatus = watchStatus;
        _alerting = alerting;
        _collectors = collectors;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_processWatches.Count == 0 && _serviceWatches.Count == 0)
        {
            _logger.LogInformation("Watchdog: no watches configured, skipping");
            return;
        }

        _logger.LogInformation("Watchdog started: {ProcessCount} process watch(es), {ServiceCount} service watch(es)",
            _processWatches.Count, _serviceWatches.Count);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _collectors.WatchIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                if (_processWatches.Count > 0)
                {
                    var processResults = await _processCollector.CollectAsync(_processWatches, ct);
                    foreach (var r in processResults)
                    {
                        var cfg = _processWatches.FirstOrDefault(c => c.Name == r.WatchName);
                        if (cfg is { AlertWhenMissing: false }) continue;

                        await EvaluateAsync(
                            "process", r.WatchName, r.IsRunning,
                            downTitle: $"Process down: {r.WatchName}",
                            downBody: $"No running instance found (expected ≥{cfg?.MinInstances ?? 1}). Host: {_machineName}",
                            recoveryTitle: $"Process recovered: {r.WatchName}",
                            severityStr: cfg?.Severity ?? "Warning", ct);
                    }
                }

                if (_serviceWatches.Count > 0)
                {
                    var serviceResults = await _serviceCollector.CollectAsync(_serviceWatches, ct);
                    foreach (var r in serviceResults)
                    {
                        var cfg = _serviceWatches.FirstOrDefault(c => c.Name == r.WatchName);

                        await EvaluateAsync(
                            "service", r.WatchName, r.IsExpectedStatus,
                            downTitle: $"Service not running: {r.WatchName}",
                            downBody: $"Service '{r.ServiceName}' status: {r.Status ?? "unknown"}. Host: {_machineName}",
                            recoveryTitle: $"Service recovered: {r.WatchName}",
                            severityStr: cfg?.Severity ?? "Warning", ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchdog iteration failed");
            }
        }
    }

    /// <summary>
    /// Emit an alert only when the observed health transitions (healthy&lt;-&gt;down),
    /// using persisted watch_status as the previous-state source. First observation
    /// of a healthy watch stays silent; first observation of a down watch alerts.
    /// </summary>
    private async Task EvaluateAsync(
        string type, string watchName, bool isHealthy,
        string downTitle, string downBody, string recoveryTitle, string severityStr, CancellationToken ct)
    {
        var key = $"{type}:{watchName}";
        var currentStatus = isHealthy ? "healthy" : "down";

        var prev = await _watchStatus.GetLatestAsync(key, ct);
        if (prev is not null && prev.Status == currentStatus) return; // no state change

        await _watchStatus.InsertAsync(
            new WatchStatusEntry(null, key, type, currentStatus, null, DateTimeOffset.UtcNow), ct);

        if (prev is null && isHealthy) return; // first observation and healthy: nothing to report

        if (!isHealthy)
        {
            var severity = string.Equals(severityStr, "Critical", StringComparison.OrdinalIgnoreCase)
                ? AlertSeverity.Critical
                : AlertSeverity.Warning;

            await _dispatcher.DispatchAsync(new AlertEvent(
                Guid.NewGuid().ToString("N"), $"{type}_watch", severity,
                downTitle, downBody, $"{key}:down", _machineName, DateTimeOffset.UtcNow), ct);
        }
        else if (_alerting.SendRecoveryAlerts)
        {
            await _dispatcher.DispatchAsync(new AlertEvent(
                Guid.NewGuid().ToString("N"), $"{type}_watch", AlertSeverity.Recovery,
                recoveryTitle, string.Empty, $"{key}:up", _machineName, DateTimeOffset.UtcNow), ct);
        }
    }
}
