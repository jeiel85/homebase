namespace LocalOpsBot.Core.Delivery;

/// <summary><see cref="IOutboundChannel.ChannelId"/> values the router knows.</summary>
public static class ChannelIds
{
    /// <summary>Device Hub local channel (TRAIN C/D); no implementation registered yet.</summary>
    public const string KdeConnect = "kdeconnect";

    /// <summary>RFC 8291-encrypted UnifiedPush to the Homebase app (DECISIONS M-19).</summary>
    public const string UnifiedPush = "unifiedpush";

    /// <summary>Telegram bot; fallback during the M-19 transition.</summary>
    public const string Telegram = "telegram";
}
