using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class EventLogPollingService : BackgroundService
{
    private readonly IEventLogWatcher _watcher;
    private readonly EventLogOptions _options;
    private readonly ILogger<EventLogPollingService> _logger;

    public EventLogPollingService(
        IEventLogWatcher watcher,
        EventLogOptions options,
        ILogger<EventLogPollingService> logger)
    {
        _watcher = watcher;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Event log polling disabled");
            return;
        }

        _logger.LogInformation("Event log polling started: {Logs}", string.Join(", ", _options.Logs));
        var interval = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                var events = await _watcher.PollAsync(_options, ct);
                if (events.Count > 0)
                    _logger.LogInformation("Event log: {Count} new event(s)", events.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event log polling failed");
            }
        }
    }
}
