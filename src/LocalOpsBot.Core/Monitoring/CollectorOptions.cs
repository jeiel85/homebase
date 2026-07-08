namespace LocalOpsBot.Core.Monitoring;

/// <summary>
/// Polling cadence for the background monitors, bound from the "collectors"
/// config section. Previously these intervals were hard-coded in each service.
/// </summary>
public sealed class CollectorOptions
{
    public int MetricIntervalSeconds { get; set; } = 60;
    public int WatchIntervalSeconds { get; set; } = 60;
    public int EventLogPollingIntervalSeconds { get; set; } = 30;
    public int CollectorTimeoutSeconds { get; set; } = 5;
}
