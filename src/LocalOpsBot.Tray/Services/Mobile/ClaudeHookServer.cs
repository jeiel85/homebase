using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Where Claude Code's PermissionRequest hook (a <c>curl.exe</c> call, see <see cref="ClaudeHook.Command"/>)
/// lands (DECISIONS M-48): a loopback-only HTTP listener of the signed-in user's Tray. With the user
/// away from the PC it hands the prompt to the Agent (<c>approval.ask</c>), which pushes it to the
/// phones, and prints the phone's Allow / Deny as the hook's output. Otherwise — user at the PC, a
/// question rather than a permission, no Agent, no answer in time, or the user comes back while it
/// waits — it answers with nothing, and Claude Code asks on the PC as usual.
/// Requests must carry the token stored in <c>%LOCALAPPDATA%\Homebase\claude-hook.json</c>, so other
/// local programs cannot use the Tray to reach the phone.
/// </summary>
internal sealed class ClaudeHookServer : IDisposable
{
    public const int DefaultPort = 47391;
    private const int MaxConcurrent = 8;

    /// <summary>Keyboard or mouse used within this long while a prompt waits: the user is back, the PC asks.</summary>
    private static readonly TimeSpan BackAtPc = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly string _token;
    private readonly Func<MobileBridgeClient?> _bridge;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public int Port { get; }

    private ClaudeHookServer(TcpListener listener, int port, string token, Func<MobileBridgeClient?> bridge)
    {
        _listener = listener;
        Port = port;
        _token = token;
        _bridge = bridge;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>Listens on 127.0.0.1, on <paramref name="preferredPort"/> or the next free one of ten.</summary>
    public static ClaudeHookServer Start(int preferredPort, string token, Func<MobileBridgeClient?> bridge)
    {
        SocketException? last = null;
        for (var port = preferredPort; port < preferredPort + 10; port++)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
                return new ClaudeHookServer(listener, port, token, bridge);
            }
            catch (SocketException ex)
            {
                last = ex;
                listener.Stop();
            }
        }
        throw new IOException($"No free local port from {preferredPort}.", last);
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            if (!await _slots.WaitAsync(0, ct))
            {
                client.Dispose(); // curl fails, prints nothing: the PC asks
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TrayLog.Warn("claude-hook", "A Claude permission request failed; the PC asks as usual.", ex);
                }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readLimit.CancelAfter(TimeSpan.FromSeconds(10));
        var (status, body) = await ClaudeHookHttp.ReadRequestAsync(stream, _token, readLimit.Token);
        var output = status == 200 && body is not null ? await DecideAsync(body, ct) : "";
        await ClaudeHookHttp.WriteResponseAsync(stream, status, output, ct);
    }

    /// <summary>The hook's stdout: a decision from the phone, or empty for "ask on the PC".</summary>
    private async Task<string> DecideAsync(string hookInput, CancellationToken ct)
    {
        if (UserIdle.Duration() < MobileBridgeClient.AwayAfter) return "";
        var bridge = _bridge();
        if (bridge is null || !bridge.IsConnected) return "";
        var request = ClaudeHook.FromInput(hookInput, bridge.SessionTitle, DateTimeOffset.UtcNow);
        if (request is null) return "";

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var back = WatchForReturnAsync(wait);
        try
        {
            var response = await bridge.RequestAsync(MobileBridge.OpApprovalAsk, request, wait.Token, ClaudeHook.WaitLimit + TimeSpan.FromSeconds(30));
            var decision = response["payload"]?.Deserialize<ApprovalAskResult>(MobileProtocol.Json)?.Decision;
            return ApprovalDecision.IsKnown(decision) ? ClaudeHook.Output(decision!) : "";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException or InvalidOperationException)
        {
            // Back at the PC, Agent gone or the wait ran out: withdraw the prompt from the phones.
            try { await bridge.SendPushAsync(MobileBridge.OpApprovalCancel, new { id = request.Id }); }
            catch (Exception cancelEx) when (cancelEx is IOException or ObjectDisposedException or OperationCanceledException) { }
            return "";
        }
        finally
        {
            wait.Cancel();
            await back;
        }
    }

    /// <summary>Cancels <paramref name="wait"/> as soon as the user touches the keyboard or mouse.</summary>
    private static async Task WatchForReturnAsync(CancellationTokenSource wait)
    {
        try
        {
            while (!wait.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), wait.Token);
                if (UserIdle.Duration() < BackAtPc)
                {
                    wait.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
