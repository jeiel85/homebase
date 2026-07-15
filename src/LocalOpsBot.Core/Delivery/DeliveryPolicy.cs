namespace LocalOpsBot.Core.Delivery;

public enum DeliveryPolicy
{
    OriginOnly,
    LocalPreferred,
    TelegramFallback,
    Both,
    LocalOnly,
    TelegramOnly
}

public enum OutboundPriority
{
    Info,
    Warning,
    Critical,
    Recovery
}
