using LocalOpsBot.Core.Commands;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Infrastructure.Commands;

public sealed class AllowedChatPolicy : IChatAuthorizationPolicy
{
    private readonly HashSet<long> _allowedChatIds;

    public AllowedChatPolicy(IOptions<TelegramOptions> options)
    {
        _allowedChatIds = new HashSet<long>(options.Value.AllowedChatIds);
    }

    public bool IsAllowed(long chatId, long? userId)
        => _allowedChatIds.Contains(chatId);
}
