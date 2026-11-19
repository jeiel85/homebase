using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Agent side of <c>\\.\pipe\Homebase.Transfer</c> (DECISIONS M-64): the Tray reads the bytes of a received file here
/// and saves it in the user's Downloads. One file per connection — a request frame, a header frame, the raw bytes —
/// so a large file never goes through the JSON bridge and the bridge (live views, approvals) is never held up.
/// Same ACL as the bridge (<see cref="TrayPipeSecurity"/>): SYSTEM, Administrators and the configured user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TransferPipeServer : BackgroundService
{
    private const int MaxInstances = 4;
    private const int BufferBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly FileShareService _files;
    private readonly MobileOptions _options;
    private readonly ILogger<TransferPipeServer> _logger;
    private readonly string _pipeName;

    /// <param name="pipeName">Tests only: a name of their own, so they never meet an installed Agent's pipe.</param>
    public TransferPipeServer(FileShareService files, IOptions<MobileOptions> options, ILogger<TransferPipeServer> logger, string? pipeName = null)
    {
        _pipeName = pipeName ?? TransferPipe.PipeName;
        _files = files;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferBytes, BufferBytes,
                    TrayPipeSecurity.Build(_options.UserSid, _logger));
            }
            catch (IOException)
            {
                await Task.Delay(1000, stoppingToken).ContinueWith(_ => { });
                running.RemoveAll(t => t.IsCompleted);
                continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                _logger.LogError(ex, "Transfer pipe could not be created; retrying in 30 s.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ContinueWith(_ => { });
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => ServeAsync(pipe, stoppingToken), CancellationToken.None));
        }
        await Task.WhenAll(running);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            TransferPipeRequest? request;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(RequestTimeout);
                request = (await MobileBridge.ReadFrameAsync(pipe, limit.Token))?.Deserialize<TransferPipeRequest>(MobileProtocol.Json);
            }
            if (_files.OpenForTray(request) is not { } open)
            {
                await MobileBridge.WriteFrameAsync(pipe, Frame(new TransferPipeHeader(false, 0, "not_found")), ct);
                return;
            }
            await using (open.Stream)
            {
                await MobileBridge.WriteFrameAsync(pipe, Frame(new TransferPipeHeader(true, open.Size)), ct);
                await open.Stream.CopyToAsync(pipe, BufferBytes, ct);
                await pipe.FlushAsync(ct);
            }
            // Let the Tray read to the end before the pipe goes away.
            try { pipe.WaitForPipeDrain(); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Transfer pipe connection ended: {Message}", ex.Message);
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }

    private static System.Text.Json.Nodes.JsonObject Frame(TransferPipeHeader header) =>
        (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(header, MobileProtocol.Json)!;
}
