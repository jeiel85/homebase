namespace LocalOpsBot.Infrastructure.Telegram;

public sealed class TelegramOptions
{
    public string BotToken { get; init; } = string.Empty;
    public IReadOnlyList<long> AllowedChatIds { get; init; } = Array.Empty<long>();
    public int PollingTimeoutSeconds { get; init; } = 30;
    public int PollingErrorBackoffSeconds { get; init; } = 10;
    public string ParseMode { get; init; } = "HTML";
    public bool DisableWebPagePreview { get; init; } = true;
    public bool RespondToUnauthorized { get; init; } = false;
}
