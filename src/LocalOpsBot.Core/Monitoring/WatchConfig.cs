namespace LocalOpsBot.Core.Monitoring;

public sealed record ProcessWatchConfig(
    string Name,
    IReadOnlyList<string> ProcessNames,
    bool AlertWhenMissing = true,
    int MinInstances = 1,
    string Severity = "Warning");

public sealed record ServiceWatchConfig(
    string Name,
    string ServiceName,
    string ExpectedStatus = "Running",
    string Severity = "Warning");
