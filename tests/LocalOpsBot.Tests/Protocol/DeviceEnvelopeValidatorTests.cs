using System.Text.Json;
using LocalOpsBot.Protocol.Messaging;
using Xunit;

namespace LocalOpsBot.Tests.Protocol;

public sealed class DeviceEnvelopeValidatorTests
{
    [Fact]
    public void Validate_AcceptsValidEnvelope()
    {
        var envelope = CreateEnvelope();
        DeviceEnvelopeValidator.Validate(envelope, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Validate_RejectsExpiredEnvelope()
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = CreateEnvelope() with
        {
            ExpiresAt = now.AddSeconds(-1)
        };

        Assert.Throws<InvalidOperationException>(
            () => DeviceEnvelopeValidator.Validate(envelope, now));
    }

    [Fact]
    public void Validate_RejectsHopLimit()
    {
        var envelope = CreateEnvelope() with
        {
            HopCount = 4,
            MaxHops = 4
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DeviceEnvelopeValidator.Validate(
                envelope,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_RejectsInvalidSha256()
    {
        var envelope = CreateEnvelope() with
        {
            Payload = new PayloadDescriptor(
                Guid.NewGuid(),
                "file.txt",
                "text/plain",
                10,
                "not-a-hash")
        };

        Assert.Throws<ArgumentException>(
            () => DeviceEnvelopeValidator.Validate(
                envelope,
                DateTimeOffset.UtcNow));
    }

    private static DeviceEnvelope CreateEnvelope()
    {
        using var document = JsonDocument.Parse("{\"value\":\"ok\"}");

        return new DeviceEnvelope(
            Guid.NewGuid(),
            1,
            "homebase.test",
            EndpointAddress.Telegram(123),
            null,
            Guid.NewGuid(),
            null,
            null,
            0,
            4,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            MessageSensitivity.Normal,
            DeliverySemantics.BestEffort,
            document.RootElement.Clone(),
            null,
            null);
    }
}
