using System.Runtime.Versioning;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Data.Models;
using LocalOpsBot.Data.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class DevMonitorBackgroundService : BackgroundService
{
    private readonly IHttpEndpointMonitor _httpMonitor;
    private readonly ITcpPortMonitor _tcpMonitor;
    private readonly IReadOnlyList<HttpEndpointConfig> _endpoints;
    private readonly IReadOnlyList<TcpPortConfig> _ports;
    private readonly IAlertDispatcher _dispatcher;
    private readonly IWatchStatusRepository _watchStatus;
    private readonly AlertingOptions _alerting;
    private readonly CollectorOptions _collectors;
    private readonly ILogger<DevMonitorBackgroundService> _logger;
    private readonly string _machineName = Environment.MachineName;

    public DevMonitorBackgroundService(
        IHttpEndpointMonitor httpMonitor,
        ITcpPortMonitor tcpMonitor,
        IEnumerable<HttpEndpointConfig> endpoints,
        IEnumerable<TcpPortConfig> ports,
        IAlertDispatcher dispatcher,
        IWatchStatusRepository watchStatus,
        AlertingOptions alerting,
        CollectorOptions collectors,
        ILogger<DevMonitorBackgroundService> logger)
    {
        _httpMonitor = httpMonitor;
        _tcpMonitor = tcpMonitor;
        _endpoints = endpoints.ToList();
        _ports = ports.ToList();
        _dispatcher = dispatcher;
        _watchStatus = watchStatus;
        _alerting = alerting;
        _collectors = collectors;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_endpoints.Count == 0 && _ports.Count == 0)
        {
            _logger.LogInformation("DevMonitor: no endpoints or ports configured, skipping");
            return;
        }

        _logger.LogInformation("DevMonitor started: {EndpointCount} endpoint(s), {PortCount} port(s)",
            _endpoints.Count, _ports.Count);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _collectors.WatchIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                foreach (var ep in _endpoints)
                {
                    var result = await _httpMonitor.CheckAsync(ep, ct);
                    if (result.Success)
                        _logger.LogDebug("Endpoint '{Name}' OK ({StatusCode}, {ResponseTimeMs}ms)",
                            result.Name, result.StatusCode, result.ResponseTimeMs);

                    await EvaluateAsync(
                        "http", result.Name, result.Success,
                        downTitle: $"Endpoint down: {result.Name}",
                        downBody: $"{result.Url} — {result.Error ?? $"HTTP {result.StatusCode}"}",
                        recoveryTitle: $"Endpoint recovered: {result.Name}", ct);
                }

                foreach (var p in _ports)
                {
                    var result = await _tcpMonitor.CheckAsync(p, ct);
                    if (result.Open)
                        _logger.LogDebug("Port '{Name}' open ({ResponseTimeMs}ms)", result.Name, result.ResponseTimeMs);

                    await EvaluateAsync(
                        "port", result.Name, result.Open,
                        downTitle: $"Port closed: {result.Name}",
                        downBody: $"{result.Host}:{result.Port} — {result.Error ?? "connection failed"}",
                        recoveryTitle: $"Port recovered: {result.Name}", ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DevMonitor iteration failed");
            }
        }
    }

    private async Task EvaluateAsync(
        string type, string name, bool isHealthy,
        string downTitle, string downBody, string recoveryTitle, CancellationToken ct)
    {
        var key = $"{type}:{name}";
        var currentStatus = isHealthy ? "healthy" : "down";

        var prev = await _watchStatus.GetLatestAsync(key, ct);
        if (prev is not null && prev.Status == currentStatus) return;

        await _watchStatus.InsertAsync(
            new WatchStatusEntry(null, key, type, currentStatus, null, DateTimeOffset.UtcNow), ct);

        if (prev is null && isHealthy) return;

        if (!isHealthy)
        {
            await _dispatcher.DispatchAsync(new AlertEvent(
                Guid.NewGuid().ToString("N"), $"{type}_monitor", AlertSeverity.Warning,
                downTitle, downBody, $"{key}:down", _machineName, DateTimeOffset.UtcNow), ct);
        }
        else if (_alerting.SendRecoveryAlerts)
        {
            await _dispatcher.DispatchAsync(new AlertEvent(
                Guid.NewGuid().ToString("N"), $"{type}_monitor", AlertSeverity.Recovery,
                recoveryTitle, string.Empty, $"{key}:up", _machineName, DateTimeOffset.UtcNow), ct);
        }
    }
}
