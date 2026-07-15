namespace LocalOpsBot.Protocol.Messaging;

public static class DeviceEnvelopeValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumHops = 8;

    public static void Validate(DeviceEnvelope envelope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Source);

        if (envelope.MessageId == Guid.Empty)
            throw new ArgumentException("MessageId is required.", nameof(envelope));

        if (envelope.TraceId == Guid.Empty)
            throw new ArgumentException("TraceId is required.", nameof(envelope));

        if (envelope.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Envelope schema {envelope.SchemaVersion} is not supported.");

        if (string.IsNullOrWhiteSpace(envelope.MessageType))
            throw new ArgumentException("MessageType is required.", nameof(envelope));

        ValidateEndpoint(envelope.Source, nameof(envelope.Source));

        if (envelope.Target is not null)
            ValidateEndpoint(envelope.Target, nameof(envelope.Target));

        if (envelope.MaxHops is < 1 or > MaximumHops)
            throw new ArgumentOutOfRangeException(nameof(envelope.MaxHops));

        if (envelope.HopCount < 0 || envelope.HopCount >= envelope.MaxHops)
            throw new ArgumentOutOfRangeException(nameof(envelope.HopCount));

        if (envelope.ExpiresAt is not null && envelope.ExpiresAt <= now)
            throw new InvalidOperationException("Envelope has expired.");

        if (envelope.Payload is { SizeBytes: < 0 })
            throw new ArgumentOutOfRangeException(nameof(envelope.Payload.SizeBytes));

        if (envelope.Payload?.Sha256 is { Length: > 0 } hash &&
            (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException(
                "Payload SHA-256 must be 64 hexadecimal characters.",
                nameof(envelope));
        }
    }

    private static void ValidateEndpoint(
        EndpointAddress endpoint,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(endpoint.TransportId))
            throw new ArgumentException("TransportId is required.", parameterName);

        if (string.IsNullOrWhiteSpace(endpoint.EndpointId))
            throw new ArgumentException("EndpointId is required.", parameterName);
    }
}
