namespace LocalOpsBot.Core.Commands;

public interface IChatAuthorizationPolicy
{
    bool IsAllowed(long chatId, long? userId);
}
