using System.IO;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

public sealed class WindowsDiskCollector : IDiskCollector
{
    public string Name => "Disk";

    public Task<CollectorResult<IReadOnlyList<DiskSnapshot>>> CollectAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType != DriveType.Ram)
                .Select(d =>
                {
                    var totalBytes = d.TotalSize;
                    var freeBytes = d.AvailableFreeSpace;
                    var usedBytes = totalBytes - freeBytes;
                    var usedPercent = totalBytes > 0 ? (double)usedBytes / totalBytes * 100 : 0;
                    return new DiskSnapshot(
                        d.Name, d.DriveType.ToString(),
                        totalBytes, freeBytes, usedBytes,
                        usedPercent, true);
                })
                .ToList();

            return Task.FromResult(CollectorResult<IReadOnlyList<DiskSnapshot>>.Ok(drives, now));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CollectorResult<IReadOnlyList<DiskSnapshot>>.Fail(
                ex.Message, DateTimeOffset.UtcNow));
        }
    }
}
