using System.Runtime.Versioning;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class EventLogPollingService : BackgroundService
{
    private readonly IEventLogWatcher _watcher;
    private readonly EventLogOptions _options;
    private readonly IAlertDispatcher _dispatcher;
    private readonly CollectorOptions _collectors;
    private readonly ILogger<EventLogPollingService> _logger;
    private readonly string _machineName = Environment.MachineName;

    public EventLogPollingService(
        IEventLogWatcher watcher,
        EventLogOptions options,
        IAlertDispatcher dispatcher,
        CollectorOptions collectors,
        ILogger<EventLogPollingService> logger)
    {
        _watcher = watcher;
        _options = options;
        _dispatcher = dispatcher;
        _collectors = collectors;
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
        var interval = TimeSpan.FromSeconds(Math.Max(5, _collectors.EventLogPollingIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                var events = await _watcher.PollAsync(_options, ct);
                if (events.Count == 0) continue;

                _logger.LogInformation("Event log: {Count} new event(s)", events.Count);

                foreach (var e in events)
                {
                    var severity = string.Equals(e.Level, "Critical", StringComparison.OrdinalIgnoreCase)
                        ? AlertSeverity.Critical
                        : AlertSeverity.Warning;

                    var title = $"{e.Level} event: {e.ProviderName ?? e.LogName}";
                    var message = Truncate(e.Message, _options.MessageMaxChars);
                    var body = $"Log: {e.LogName} · Event ID: {e.EventId} · {e.TimeCreated:yyyy-MM-dd HH:mm:ss}"
                             + (string.IsNullOrWhiteSpace(message) ? string.Empty : $"\n{message}");

                    await _dispatcher.DispatchAsync(new AlertEvent(
                        Guid.NewGuid().ToString("N"),
                        "event_log",
                        severity,
                        title,
                        body,
                        $"eventlog:{e.LogName}:{e.RecordId}",
                        e.MachineName ?? _machineName,
                        DateTimeOffset.UtcNow), ct);
                }
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

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        value = value.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }
}
