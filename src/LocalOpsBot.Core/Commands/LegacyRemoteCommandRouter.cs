namespace LocalOpsBot.Core.Commands;

public sealed class LegacyRemoteCommandRouter : IRemoteCommandRouter
{
    private readonly ICommandRouter _legacyRouter;

    public LegacyRemoteCommandRouter(ICommandRouter legacyRouter)
    {
        _legacyRouter = legacyRouter;
    }

    public async Task<RemoteCommandResult> RouteAsync(
        RemoteCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var legacy = new BotCommand(
            command.Name,
            command.Args,
            command.LegacyChatId ?? 0,
            command.LegacyUserId,
            command.RawInput,
            command.ReceivedAt);

        var result = await _legacyRouter.RouteAsync(legacy, ct);

        return new RemoteCommandResult(
            result.Success,
            result.ResponseText,
            result.SendResponse,
            result.Error);
    }
}
