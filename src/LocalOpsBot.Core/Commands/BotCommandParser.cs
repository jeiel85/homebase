namespace LocalOpsBot.Core.Commands;

public static class BotCommandParser
{
    private const int MaxMessageLength = 2048;

    public static BotCommand? Parse(string rawText, long chatId, long? userId, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(rawText) || rawText.Length > MaxMessageLength)
            return null;

        var trimmed = rawText.Trim();
        if (!trimmed.StartsWith('/'))
            return null;

        var spaceIdx = trimmed.IndexOf(' ');
        var cmdPart = spaceIdx >= 0 ? trimmed[..spaceIdx] : trimmed;
        var argsPart = spaceIdx >= 0 ? trimmed[(spaceIdx + 1)..] : string.Empty;

        var cmdName = cmdPart[1..]; // strip leading '/'

        // strip bot username suffix: /status@my_bot -> status
        var atIdx = cmdName.IndexOf('@');
        if (atIdx >= 0)
            cmdName = cmdName[..atIdx];

        cmdName = cmdName.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cmdName))
            return null;

        var args = string.IsNullOrWhiteSpace(argsPart)
            ? Array.Empty<string>()
            : argsPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return new BotCommand(cmdName, args, chatId, userId, rawText, receivedAt);
    }
}
