namespace LocalOpsBot.Core.Delivery;

public enum DeliveryPolicy
{
    OriginOnly,
    LocalPreferred,
    TelegramFallback,
    Both,
    LocalOnly,
    TelegramOnly,

    /// <summary>
    /// Local (KDE Connect) then UnifiedPush, never Telegram: for content that must not reach a
    /// plaintext cloud, such as forwarded Windows toasts (DECISIONS M-19).
    /// </summary>
    EncryptedOnly
}

public enum OutboundPriority
{
    Info,
    Warning,
    Critical,
    Recovery
}
