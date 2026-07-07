namespace LocalOpsBot.Data.Models;

public sealed record MetricSampleEntry(
    long? Id,
    DateTimeOffset CollectedAt,
    double? CpuUsagePercent,
    double? MemoryUsagePercent,
    long? TotalMemoryBytes,
    long? AvailableMemoryBytes,
    long? UptimeSeconds,
    string? DiskJson,
    string? NetworkJson);
