using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

public sealed class HostInfoProvider : IHostInfoProvider
{
    public Task<HostInfoRecord> GetHostInfoAsync(CancellationToken ct)
    {
        var machineName = Environment.MachineName;
        var ip = GetPrimaryIPv4();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var bootTime = DateTimeOffset.UtcNow - uptime;
        var osVersion = Environment.OSVersion.VersionString;

        return Task.FromResult(new HostInfoRecord(
            machineName, ip, bootTime, uptime, osVersion));
    }

    private static string? GetPrimaryIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
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
