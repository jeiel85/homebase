using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Owns the Tray's single <see cref="MobileBridgeClient"/>. Started at Tray startup only when
/// <c>mobile:enabled</c> is set, or by the "Connect phone" window right after enabling.
/// </summary>
internal static class MobileHost
{
    private static MobileBridgeClient? _client;

    public static MobileBridgeClient? Client => _client;

    public static bool IsEnabled() => TrayConfig.Load().GetSection("mobile").GetValue<bool>("enabled");

    public static MobileBridgeClient EnsureStarted()
    {
        if (_client is not null) return _client;
        var claude = TrayConfig.Load().GetSection("mobile").GetValue("claudeSessions", true);
        _client = new MobileBridgeClient(claude);
        return _client;
    }

    public static void StartIfEnabled()
    {
        if (IsEnabled()) EnsureStarted();
    }

    public static void Stop()
    {
        _client?.Dispose();
        _client = null;
    }
}
