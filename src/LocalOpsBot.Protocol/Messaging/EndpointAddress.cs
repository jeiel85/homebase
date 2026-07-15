namespace LocalOpsBot.Protocol.Messaging;

public sealed record EndpointAddress(
    string TransportId,
    string EndpointId,
    string? DeviceId = null)
{
    public static EndpointAddress Telegram(long chatId)
        => new("telegram", $"chat:{chatId}");

    public static EndpointAddress KdeConnect(string deviceId)
        => new("kdeconnect", $"device:{deviceId}", deviceId);

    public static EndpointAddress LocalSession(int sessionId)
        => new("local-session", $"session:{sessionId}");
}
