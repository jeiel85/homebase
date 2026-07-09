using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Tray.Services;

public sealed class NotificationBridgeClient : INotificationBridgeClient, IDisposable
{
    private NamedPipeClientStream? _pipe;
    private const string PipeName = "Homebase.NotificationPipe";
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 1000;
    private const int ConnectTimeoutMs = 3000;

    public async Task SendAsync(ToastNotificationEvent notification, CancellationToken ct)
    {
        if (_pipe == null || !_pipe.IsConnected)
        {
            await ConnectAsync(ct);
        }

        if (_pipe == null || !_pipe.IsConnected)
            return;

        try
        {
            var message = new ToastNotificationPipeMessage(
                SchemaVersion: 1,
                Type: "toast_notification",
                EventId: notification.EventId,
                SourceApp: notification.SourceApp,
                Title: notification.Title,
                Body: notification.Body,
                CreatedAt: notification.CreatedAt,
                Sensitivity: notification.Sensitivity);

            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            var lengthBytes = BitConverter.GetBytes(bytes.Length);

            await _pipe.WriteAsync(lengthBytes, ct);
            await _pipe.WriteAsync(bytes, ct);
            await _pipe.FlushAsync(ct);
        }
        catch
        {
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < MaxRetries; i++)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(ConnectTimeoutMs, ct);
                return;
            }
            catch
            {
                _pipe?.Dispose();
                _pipe = null;
                await Task.Delay(RetryDelayMs, ct);
            }
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
    }
}
