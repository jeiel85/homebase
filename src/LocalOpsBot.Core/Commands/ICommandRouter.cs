namespace LocalOpsBot.Core.Commands;

public interface ICommandRouter
{
    Task<CommandResult> RouteAsync(BotCommand command, CancellationToken ct);
}
