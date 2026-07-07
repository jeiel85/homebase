using System.Diagnostics;
using System.Net.Sockets;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

public sealed class TcpPortMonitor : ITcpPortMonitor
{
    public async Task<TcpPortResult> CheckAsync(TcpPortConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await tcp.ConnectAsync(config.Host, config.Port, linkedCts.Token);
            sw.Stop();
            return new TcpPortResult(config.Name, config.Host, config.Port, true, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TcpPortResult(config.Name, config.Host, config.Port, false, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
