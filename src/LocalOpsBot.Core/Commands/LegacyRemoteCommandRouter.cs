namespace LocalOpsBot.Core.Commands;

/// <summary>
/// Adapts transport-neutral <see cref="RemoteCommand"/>s to the Telegram-era <see cref="ICommandRouter"/>.
/// <para><b>Not wired yet (intentional):</b> it is registered as <see cref="IRemoteCommandRouter"/>, but
/// nothing resolves it — Telegram commands still go straight to <see cref="ICommandRouter"/> from
/// <c>TelegramPollingService</c>. It is kept for the Device Hub's KDE Connect ingress (TRAIN-C,
/// <c>docs/DEVICE_HUB_IMPLEMENTATION_PLAN.md</c>), which will route commands through it. Do not remove
/// it as dead code, and do not assume a command reaching it today.</para>
/// </summary>
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
