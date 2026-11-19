using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Owns the Tray's single <see cref="MobileBridgeClient"/>. Started at Tray startup only when
/// <c>mobile:enabled</c> is set, or by the Settings › Phone tab right after enabling.
/// </summary>
internal static class MobileHost
{
    private static MobileBridgeClient? _client;

    public static MobileBridgeClient? Client => _client;

    /// <summary>A phone is viewing this PC right now (the Agent said <c>watch on</c>).</summary>
    public static bool IsWatching => _client?.IsWatching == true;

    /// <summary>
    /// <see cref="IsWatching"/> changed. Static so the tray icon can subscribe at startup, before the
    /// client exists (it is created later when the Phone tab enables the feature). Background thread.
    /// </summary>
    public static event Action<bool>? WatchingChanged;

    /// <summary>A relayed notification to show (DECISIONS M-28). Background thread.</summary>
    public static event Action<LocalOpsBot.Core.Mobile.TrayToast>? ToastRequested;

    /// <summary>
    /// Puts text another device sent on the clipboard and says whether it did (DECISIONS M-62). Static so the tray
    /// icon can set it at startup, before the client exists. Called on a background thread.
    /// </summary>
    public static Func<LocalOpsBot.Core.Mobile.TrayClip, Task<bool>>? ClipHandler { get; set; }

    /// <summary>Saves files another device sent (DECISIONS M-64). Static for the same reason. Background thread.</summary>
    public static Func<LocalOpsBot.Core.Mobile.TrayTransfer, Task<LocalOpsBot.Core.Mobile.TrayTransferSaved>>? FilesHandler { get; set; }

    public static bool IsEnabled() => TrayConfig.Load().GetSection("mobile").GetValue<bool>("enabled");

    public static MobileBridgeClient EnsureStarted()
    {
        if (_client is not null) return _client;
        var claude = TrayConfig.Load().GetSection("mobile").GetValue("claudeSessions", true);
        _client = new MobileBridgeClient(claude);
        _client.WatchingChanged += on => WatchingChanged?.Invoke(on);
        _client.ToastRequested += t => ToastRequested?.Invoke(t);
        _client.ClipHandler = c => ClipHandler is { } handler ? handler(c) : Task.FromResult(false);
        _client.FilesHandler = t => FilesHandler is { } handler
            ? handler(t)
            : Task.FromResult(new LocalOpsBot.Core.Mobile.TrayTransferSaved(t.Id, false, "no handler"));
        _client.AliasesReceived += DeviceNames.Apply;
        // The client connects from its constructor: the first table may already be in (Apply is idempotent).
        if (_client.LastAliases is { } aliases) DeviceNames.Apply(aliases);
        // Phone approvals for Claude Code (M-48) need the bridge; listen again if the user turned them on.
        ClaudeHookSetup.Resume();
        return _client;
    }

    public static void StartIfEnabled()
    {
        if (IsEnabled()) EnsureStarted();
    }

    public static void Stop()
    {
        var wasWatching = IsWatching;
        ClaudeHookSetup.Stop();
        _client?.Dispose();
        _client = null;
        if (wasWatching) WatchingChanged?.Invoke(false);
    }
}
