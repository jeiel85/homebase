namespace LocalOpsBot.Core.Commands;

public sealed record BotCommand(
    string Name,
    IReadOnlyList<string> Args,
    long ChatId,
    long? UserId,
    string RawText,
    DateTimeOffset ReceivedAt);
