namespace LocalOpsBot.Data.Models;

public sealed record NotificationEventEntry(
    long? Id,
    string EventId,
    string SourceApp,
    string? Title,
    string? Body,
    string? BodyHash,
    string Sensitivity,
    bool Forwarded,
    string? DroppedReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset ProcessedAt);
