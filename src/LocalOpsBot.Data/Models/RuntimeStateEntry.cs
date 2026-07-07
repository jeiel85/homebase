namespace LocalOpsBot.Data.Models;

public sealed record RuntimeStateEntry(
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);
