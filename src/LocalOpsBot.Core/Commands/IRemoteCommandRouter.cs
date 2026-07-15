namespace LocalOpsBot.Core.Commands;

public interface IRemoteCommandRouter
{
    Task<RemoteCommandResult> RouteAsync(
        RemoteCommand command,
        CancellationToken ct);
}
