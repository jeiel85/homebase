namespace LocalOpsBot.Core.Monitoring;

public sealed record HostInfoRecord(
    string MachineName,
    string? PrimaryIPv4,
    DateTimeOffset BootTime,
    TimeSpan Uptime,
    string? OsVersion);
