using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessCollector : IProcessCollector
{
    public async Task<IReadOnlyList<ProcessWatchStatus>> CollectAsync(
        IReadOnlyList<ProcessWatchConfig> watches, CancellationToken ct)
    {
        var results = new List<ProcessWatchStatus>();
        foreach (var watch in watches)
            results.Add(await CollectSingleAsync(watch, ct));
        return results;
    }

    private static Task<ProcessWatchStatus> CollectSingleAsync(
        ProcessWatchConfig watch, CancellationToken ct)
    {
        try
        {
            var instances = new List<ProcessInstanceInfo>();
            foreach (var name in watch.ProcessNames)
            {
                var normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? name[..^4] : name;
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName(normalized))
                {
                    string? mainModule = null;
                    try { mainModule = proc.MainModule?.FileName; }
                    catch { }

                    instances.Add(new ProcessInstanceInfo(
                        proc.Id, proc.ProcessName, mainModule,
                        proc.StartTime.ToUniversalTime(),
                        proc.WorkingSet64));
                }
            }

            var isRunning = instances.Count >= watch.MinInstances;
            return Task.FromResult(new ProcessWatchStatus(
                watch.Name, watch.ProcessNames, isRunning,
                instances.Count, instances));
        }
        catch
        {
            return Task.FromResult(new ProcessWatchStatus(
                watch.Name, watch.ProcessNames, false, 0,
                Array.Empty<ProcessInstanceInfo>())
            { });
        }
    }
}
