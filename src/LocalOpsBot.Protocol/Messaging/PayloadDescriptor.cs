namespace LocalOpsBot.Protocol.Messaging;

public sealed record PayloadDescriptor(
    Guid TransferId,
    string? FileName,
    string? MimeType,
    long SizeBytes,
    string? Sha256);
