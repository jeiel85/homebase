using LocalOpsBot.Core.Alerts;

namespace LocalOpsBot.Core.Commands;

public sealed class UnmuteCommandHandler : ICommandHandler
{
    private readonly IStateStore _stateStore;
    private const string MutedUntilKey = "alert.muted_until";

    public string CommandName => "unmute";
    public string Description => "Re-enable alerts";

    public UnmuteCommandHandler(IStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        await _stateStore.SetAsync(MutedUntilKey, DateTime.UtcNow.ToString("O"), ct);
        return new CommandResult(true, "\U0001f514 Alerts unmuted.");
    }
}
