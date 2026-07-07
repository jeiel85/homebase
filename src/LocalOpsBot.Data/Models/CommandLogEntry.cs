namespace LocalOpsBot.Data.Models;

public sealed record CommandLogEntry(
    long? Id,
    long ChatId,
    long? UserId,
    string Command,
    string? ArgsJson,
    string? RawText,
    string Status,
    string? Error,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt);
