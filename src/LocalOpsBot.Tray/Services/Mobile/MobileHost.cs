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

    public static bool IsEnabled() => TrayConfig.Load().GetSection("mobile").GetValue<bool>("enabled");

    public static MobileBridgeClient EnsureStarted()
    {
        if (_client is not null) return _client;
        var claude = TrayConfig.Load().GetSection("mobile").GetValue("claudeSessions", true);
        _client = new MobileBridgeClient(claude);
        _client.WatchingChanged += on => WatchingChanged?.Invoke(on);
        _client.ToastRequested += t => ToastRequested?.Invoke(t);
        return _client;
    }

    public static void StartIfEnabled()
    {
        if (IsEnabled()) EnsureStarted();
    }

    public static void Stop()
    {
        var wasWatching = IsWatching;
        _client?.Dispose();
        _client = null;
        if (wasWatching) WatchingChanged?.Invoke(false);
    }
}
