using System.Management;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemMetricsCollector : ISystemMetricsCollector
{
    public string Name => "SystemMetrics";

    public Task<CollectorResult<SystemMetricSnapshot>> CollectAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cpu = GetCpuUsage();
            var (totalMem, freeMem) = GetMemoryInfo();
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var hostName = Environment.MachineName;
            var osVersion = Environment.OSVersion.VersionString;

            double? memPct = totalMem.HasValue && totalMem.Value > 0
                ? (double)(totalMem.Value - (freeMem ?? 0)) / totalMem.Value * 100
                : null;

            var snapshot = new SystemMetricSnapshot(
                now, cpu, totalMem, freeMem, memPct,
                uptime, hostName, osVersion);

            return Task.FromResult(CollectorResult<SystemMetricSnapshot>.Ok(snapshot, now));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CollectorResult<SystemMetricSnapshot>.Fail(
                ex.Message, DateTimeOffset.UtcNow));
        }
    }

    private static double? GetCpuUsage()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LoadPercentage FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var val = obj["LoadPercentage"];
                if (val != null)
                    return Convert.ToDouble(val);
            }
        }
        catch { }
        return null;
    }

    private static (long? totalBytes, long? freeBytes) GetMemoryInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var totalKb = obj["TotalVisibleMemorySize"];
                var freeKb = obj["FreePhysicalMemory"];
                if (totalKb != null && freeKb != null)
                {
                    return (Convert.ToInt64(totalKb) * 1024L, Convert.ToInt64(freeKb) * 1024L);
                }
            }
        }
        catch { }
        return (null, null);
    }
}
