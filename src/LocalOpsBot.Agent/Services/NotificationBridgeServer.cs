using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class NotificationBridgeServer : INotificationBridgeServer, IHostedService
{
    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource? _cts;
    private readonly ILogger<NotificationBridgeServer> _logger;
    private const string PipeName = "Homebase.NotificationPipe";

    public event Action<ToastNotificationEvent>? NotificationReceived;

    public NotificationBridgeServer(ILogger<NotificationBridgeServer> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ListenAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_pipe != null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        _logger.LogInformation("Notification pipe server starting");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await _pipe.WaitForConnectionAsync(ct);
                _logger.LogInformation("Tray connected to notification pipe");

                await ReadLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe connection error, restarting listener");
                await Task.Delay(1000, ct);
            }
            finally
            {
                if (_pipe?.IsConnected == true)
                    _pipe.Disconnect();
                _pipe?.Dispose();
                _pipe = null;
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4];
        while (!ct.IsCancellationRequested && _pipe!.IsConnected)
        {
            var bytesRead = await _pipe.ReadAsync(buffer, ct);
            if (bytesRead < 4) break;

            var msgLength = BitConverter.ToInt32(buffer);
            if (msgLength <= 0 || msgLength > 65536) break;

            var msgBytes = new byte[msgLength];
            var totalRead = 0;
            while (totalRead < msgLength)
            {
                var read = await _pipe.ReadAsync(msgBytes.AsMemory(totalRead, msgLength - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead < msgLength) break;

            var json = Encoding.UTF8.GetString(msgBytes);
            var message = JsonSerializer.Deserialize<ToastNotificationPipeMessage>(json);
            if (message == null) continue;

            var notificationEvent = new ToastNotificationEvent(
                message.EventId, message.SourceApp, message.Title, message.Body,
                message.CreatedAt, message.EventId, message.Sensitivity);

            NotificationReceived?.Invoke(notificationEvent);
        }
    }
}
