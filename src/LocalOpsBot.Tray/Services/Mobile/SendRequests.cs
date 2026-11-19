using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Explorer's "Send to › Homebase" starts <c>Homebase.Tray.exe --send &lt;paths&gt;</c> (DECISIONS M-66). When the tray is
/// already running in this session, that second process hands the paths to it over <c>\\.\pipe\Homebase.Tray.Send.&lt;session&gt;</c>
/// and exits. The pipe is this user's only (<see cref="PipeOptions.CurrentUserOnly"/>), and what comes through it only opens
/// the "send to" picker: nothing leaves this PC until the user picks a device there.
/// </summary>
internal static class SendRequests
{
    /// <summary>A path list larger than this is refused (Windows' command line is 32K characters).</summary>
    internal const int MaxFrameBytes = 256 * 1024;
    internal const int MaxPaths = 1000;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public static string PipeName => $"Homebase.Tray.Send.{Process.GetCurrentProcess().SessionId}";

    /// <summary>The paths after <c>--send</c>; null when the argument isn't there.</summary>
    public static IReadOnlyList<string>? PathsFrom(IReadOnlyList<string> args)
    {
        var at = args.ToList().IndexOf(SendToShortcut.Argument);
        if (at < 0) return null;
        return Clean(args.Skip(at + 1));
    }

    /// <summary>Full paths only, each once (Explorer gives full paths; anything else is ignored).</summary>
    internal static IReadOnlyList<string> Clean(IEnumerable<string> paths) => paths
        .Select(p => p.Trim().Trim('"'))
        .Where(p => p.Length > 0 && Path.IsPathFullyQualified(p))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxPaths)
        .ToList();

    /// <summary>
    /// Hands <paramref name="paths"/> to the tray running in this session. False when none answers (not running, or one too
    /// old to listen). Lets that tray bring its picker to the front: Explorer gave this process the right, it passes it on.
    /// </summary>
    public static bool Forward(IReadOnlyList<string> paths, TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)timeout.TotalMilliseconds);
            AllowSetForegroundWindow(AsfwAny);
            var json = JsonSerializer.SerializeToUtf8Bytes(paths);
            var head = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(head, json.Length);
            pipe.Write(head);
            pipe.Write(json);
            pipe.Flush();
            return pipe.ReadByte() == 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens until <paramref name="ct"/> ends; each path list that arrives goes to <paramref name="onPaths"/>.</summary>
    public static async Task ServeAsync(Action<IReadOnlyList<string>> onPaths, CancellationToken ct, string? pipeName = null)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TrayLog.Warn("Files", $"The Send to pipe is taken ({ex.Message}); trying again in a minute.");
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            await using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(ct);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ReadTimeout);
                    var paths = await ReadAsync(server, timeout.Token);
                    if (paths is { Count: > 0 }) onPaths(paths);
                    await server.WriteAsync(new[] { (byte)(paths is null ? 0 : 1) }, timeout.Token);
                    await server.FlushAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
                {
                    TrayLog.Warn("Files", $"A Send to request was dropped: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    internal static async Task<IReadOnlyList<string>?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length is <= 0 or > MaxFrameBytes) return null;
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        var paths = JsonSerializer.Deserialize<List<string>>(body);
        return paths is null ? null : Clean(paths);
    }
}
