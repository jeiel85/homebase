using System.Net.NetworkInformation;
using System.Net.Sockets;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

public sealed class WindowsNetworkStatusChecker : INetworkStatusChecker
{
    public string Name => "Network";

    public Task<CollectorResult<NetworkStatusSnapshot>> CollectAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var isOnline = adapters.Count > 0;
            var primaryIpv4 = GetPrimaryIPv4(adapters);
            var activeNames = adapters.Select(a => a.Name).ToList();

            var snapshot = new NetworkStatusSnapshot(
                isOnline, primaryIpv4, null, activeNames, null, null);

            return Task.FromResult(CollectorResult<NetworkStatusSnapshot>.Ok(snapshot, now));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CollectorResult<NetworkStatusSnapshot>.Fail(
                ex.Message, DateTimeOffset.UtcNow));
        }
    }

    private static string? GetPrimaryIPv4(List<NetworkInterface> adapters)
    {
        try
        {
            return adapters
                .SelectMany(a => a.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(ua => ua.Address.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
