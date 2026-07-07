using System.Runtime.Versioning;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;
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
    private readonly ILogger<WatchdogBackgroundService> _logger;

    public WatchdogBackgroundService(
        IProcessCollector processCollector,
        IWindowsServiceCollector serviceCollector,
        IEnumerable<ProcessWatchConfig> processWatches,
        IEnumerable<ServiceWatchConfig> serviceWatches,
        ILogger<WatchdogBackgroundService> logger)
    {
        _processCollector = processCollector;
        _serviceCollector = serviceCollector;
        _processWatches = processWatches.ToList();
        _serviceWatches = serviceWatches.ToList();
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

        var interval = TimeSpan.FromSeconds(60);

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
                        if (!r.IsRunning)
                            _logger.LogWarning("Process watch '{WatchName}' is missing", r.WatchName);
                    }
                }

                if (_serviceWatches.Count > 0)
                {
                    var serviceResults = await _serviceCollector.CollectAsync(_serviceWatches, ct);
                    foreach (var r in serviceResults)
                    {
                        if (!r.IsExpectedStatus)
                            _logger.LogWarning("Service watch '{WatchName}' ({ServiceName}) status = {Status}",
                                r.WatchName, r.ServiceName, r.Status);
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
}
