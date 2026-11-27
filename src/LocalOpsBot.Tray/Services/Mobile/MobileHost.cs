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
    public static Func<LocalOpsBot.Core.Mobile.TrayClip, Task<LocalOpsBot.Core.Mobile.TrayClipOutcome>>? ClipHandler { get; set; }

    /// <summary>Saves files another device sent (DECISIONS M-64). Static for the same reason. Background thread.</summary>
    public static Func<LocalOpsBot.Core.Mobile.TrayTransfer, Task<LocalOpsBot.Core.Mobile.TrayTransferSaved>>? FilesHandler { get; set; }

    /// <summary>Where files this PC sends stand (DECISIONS M-65). Background thread.</summary>
    public static event Action<LocalOpsBot.Core.Mobile.FilesProgress>? FilesProgress;

    /// <summary>Carries out a remote command (M-69); set by the tray icon.</summary>
    public static Func<LocalOpsBot.Core.Mobile.TrayCommand, Task<bool>>? CommandHandler { get; set; }

    /// <summary>A restart a phone asked for was scheduled (its time) or called off (null), and by whom (M-69).</summary>
    public static event Action<DateTimeOffset?, string>? RestartNotice;

    /// <summary>Calls off a restart under way (M-69); false when the Agent isn't reachable or nothing was pending.</summary>
    public static async Task<bool> CancelRestartAsync()
    {
        if (_client is not { IsConnected: true } client) return false;
        try { return (bool?)(await client.RequestAsync(LocalOpsBot.Core.Mobile.MobileBridge.OpCommandCancel))["payload"] == true; }
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or InvalidOperationException) { return false; }
    }

    public static bool IsEnabled() => TrayConfig.Load().GetSection("mobile").GetValue<bool>("enabled");

    public static MobileBridgeClient EnsureStarted()
    {
        if (_client is not null) return _client;
        var claude = TrayConfig.Load().GetSection("mobile").GetValue("claudeSessions", true);
        _client = new MobileBridgeClient(claude);
        _client.WatchingChanged += on => WatchingChanged?.Invoke(on);
        _client.ToastRequested += t => ToastRequested?.Invoke(t);
        _client.ClipHandler = c => ClipHandler is { } handler ? handler(c) : Task.FromResult(LocalOpsBot.Core.Mobile.TrayClipOutcome.NotPasted);
        _client.FilesProgressReceived += p => FilesProgress?.Invoke(p);
        _client.CommandHandler = c => CommandHandler is { } run ? run(c) : Task.FromResult(false);
        _client.RestartNotice += (at, by) => RestartNotice?.Invoke(at, by);
        CustomCommands.Changed += () => _ = _client?.PublishCommandsAsync();
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
