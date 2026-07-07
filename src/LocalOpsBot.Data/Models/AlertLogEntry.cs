namespace LocalOpsBot.Data.Models;

public sealed record AlertLogEntry(
    long? Id,
    string AlertId,
    string Kind,
    string Severity,
    string Title,
    string? Body,
    string? DedupKey,
    string? Source,
    string Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);
