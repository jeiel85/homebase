namespace LocalOpsBot.Core.Commands;

public sealed class CommandRouter : ICommandRouter
{
    private readonly Dictionary<string, ICommandHandler> _handlers;

    public CommandRouter(IEnumerable<ICommandHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.CommandName, StringComparer.OrdinalIgnoreCase);
    }

    public Task<CommandResult> RouteAsync(BotCommand command, CancellationToken ct)
    {
        if (_handlers.TryGetValue(command.Name, out var handler))
            return handler.HandleAsync(command, ct);

        return Task.FromResult(new CommandResult(
            false,
            $"Unknown command: /{command.Name}\nUse /help to see available commands.",
            SendResponse: true));
    }
}
