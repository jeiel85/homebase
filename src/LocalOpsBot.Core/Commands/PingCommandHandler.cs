namespace LocalOpsBot.Core.Commands;

public sealed class PingCommandHandler : ICommandHandler
{
    public string CommandName => "ping";
    public string Description => "Bot health check";

    public Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            command.ReceivedAt, "Korea Standard Time");
        var response = $"pong\n{Environment.MachineName} | {now:yyyy-MM-dd HH:mm:ss KST}";

        return Task.FromResult(new CommandResult(true, response));
    }
}
