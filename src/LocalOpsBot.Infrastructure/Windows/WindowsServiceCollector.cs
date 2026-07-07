using System.Runtime.Versioning;
using System.ServiceProcess;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceCollector : IWindowsServiceCollector
{
    public Task<IReadOnlyList<WindowsServiceWatchStatus>> CollectAsync(
        IReadOnlyList<ServiceWatchConfig> watches, CancellationToken ct)
    {
        var results = new List<WindowsServiceWatchStatus>();
        foreach (var watch in watches)
            results.Add(CollectSingle(watch));
        return Task.FromResult<IReadOnlyList<WindowsServiceWatchStatus>>(results);
    }

    private static WindowsServiceWatchStatus CollectSingle(ServiceWatchConfig watch)
    {
        try
        {
            using var controller = new ServiceController(watch.ServiceName);
            var status = controller.Status.ToString();
            return new WindowsServiceWatchStatus(
                watch.Name, watch.ServiceName, controller.DisplayName,
                status, string.Equals(status, watch.ExpectedStatus, StringComparison.OrdinalIgnoreCase),
                null);
        }
        catch (InvalidOperationException)
        {
            return new WindowsServiceWatchStatus(
                watch.Name, watch.ServiceName, null, "NotFound",
                false, "Service not found");
        }
        catch (Exception ex)
        {
            return new WindowsServiceWatchStatus(
                watch.Name, watch.ServiceName, null, "Unknown",
                false, ex.Message);
        }
    }
}
