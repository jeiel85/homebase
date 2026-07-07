using LocalOpsBot.Core.Updates;

namespace LocalOpsBot.Core.Commands;

public sealed class UpdateCommandHandler : ICommandHandler
{
    private readonly UpdateService _updater;

    public string CommandName => "update";
    public string Description => "Check for and apply updates";

    public UpdateCommandHandler(UpdateService updater) => _updater = updater;

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var currentVer = _updater.GetCurrentVersionString();

        try
        {
            var info = await _updater.CheckForUpdateAsync(ct);
            if (info == null)
                return new CommandResult(true, $"<b>\u2705 Up-to-date</b>\nCurrent version: {currentVer}");

            var lines = new List<string>
            {
                $"<b>\ud83d\udce1 Update available: {info.Version}</b>",
                $"Current: {currentVer}",
                $"Published: {info.PublishedAt:yyyy-MM-dd}",
                "",
                "Applying update..."
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var zip = await _updater.DownloadUpdateAsync(info.DownloadUrl, null, CancellationToken.None);
                    _updater.ApplyUpdate(zip);
                }
                catch { }
            }, CancellationToken.None);

            return new CommandResult(true, string.Join("\n", lines));
        }
        catch (UpdateCheckException ex)
        {
            return new CommandResult(true,
                $"<b>\u26a0\ufe0f Update check failed</b>\n" +
                $"{ex.Kind}: {ex.Message}");
        }
    }
}
