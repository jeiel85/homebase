using LocalOpsBot.Protocol.Messaging;

namespace LocalOpsBot.Core.Delivery;

public sealed record OutboundNotification(
    Guid NotificationId,
    string Category,
    OutboundPriority Priority,
    string Title,
    string? Body,
    string DedupKey,
    MessageSensitivity Sensitivity,
    DeliveryPolicy Policy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    EndpointAddress? Origin = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
