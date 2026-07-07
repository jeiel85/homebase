namespace LocalOpsBot.Core.Commands;

public interface ICommandHandler
{
    string CommandName { get; }
    string Description { get; }
    Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct);
}

public sealed record CommandResult(
    bool Success,
    string ResponseText,
    bool SendResponse = true,
    string? Error = null);
