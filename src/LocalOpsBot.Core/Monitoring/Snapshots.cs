namespace LocalOpsBot.Core.Monitoring;

public sealed record SystemMetricSnapshot(
    DateTimeOffset CollectedAt,
    double? CpuUsagePercent,
    long? TotalMemoryBytes,
    long? AvailableMemoryBytes,
    double? MemoryUsagePercent,
    TimeSpan Uptime,
    string HostName,
    string? OsVersion);

public sealed record DiskSnapshot(
    string Name,
    string DriveType,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    double UsedPercent,
    bool IsReady);

public sealed record NetworkStatusSnapshot(
    bool IsOnline,
    string? PrimaryIPv4,
    string? PrimaryIPv6,
    IReadOnlyList<string> ActiveAdapters,
    long? PingLatencyMs,
    string? FailureReason);
