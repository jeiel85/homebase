using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class DiskCommandHandler : ICommandHandler
{
    private readonly IDiskCollector _disk;

    public string CommandName => "disk";
    public string Description => "Disk usage by drive";

    public DiskCommandHandler(IDiskCollector disk) => _disk = disk;

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var result = await _disk.CollectAsync(ct);

        if (!result.Success || result.Snapshot == null || result.Snapshot.Count == 0)
            return new CommandResult(false, "Disk information unavailable.");

        var lines = new List<string> { "<b>\U0001f4be Disk Status</b>\n" };

        foreach (var d in result.Snapshot)
        {
            if (!d.IsReady) continue;

            var totalGb = d.TotalBytes / (1024.0 * 1024 * 1024);
            var usedGb = d.UsedBytes / (1024.0 * 1024 * 1024);
            var freeGb = d.FreeBytes / (1024.0 * 1024 * 1024);
            var label = d.Name.EndsWith('\\') ? d.Name : d.Name + "\\";

            lines.Add($"{label} {usedGb:F1} / {totalGb:F1} GB used ({d.UsedPercent:F1}%)");
            lines.Add($"Free: {freeGb:F1} GB");
            lines.Add($"Status: OK\n");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }
}
