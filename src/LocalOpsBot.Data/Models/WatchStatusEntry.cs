namespace LocalOpsBot.Data.Models;

public sealed record WatchStatusEntry(
    long? Id,
    string WatchName,
    string WatchType,
    string Status,
    string? StatusJson,
    DateTimeOffset ChangedAt);
