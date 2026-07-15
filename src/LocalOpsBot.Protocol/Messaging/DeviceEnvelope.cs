using System.Text.Json;

namespace LocalOpsBot.Protocol.Messaging;

public enum MessageSensitivity
{
    Normal,
    Private,
    Sensitive,
    Secret
}

public enum DeliverySemantics
{
    BestEffort,
    AtLeastOnce,
    Acknowledged
}

public sealed record DeviceEnvelope(
    Guid MessageId,
    int SchemaVersion,
    string MessageType,
    EndpointAddress Source,
    EndpointAddress? Target,
    Guid TraceId,
    Guid? CorrelationId,
    Guid? CausationId,
    int HopCount,
    int MaxHops,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    MessageSensitivity Sensitivity,
    DeliverySemantics Delivery,
    JsonElement Body,
    PayloadDescriptor? Payload,
    IReadOnlyDictionary<string, string>? Metadata);
