using System.IO;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services.Mobile;

namespace LocalOpsBot.Tray.Dashboard;

/// <summary>Where the dashboard gets devices and live views: the Agent bridge, or made-up data for a design preview.</summary>
internal interface IDashboardSource
{
    /// <summary>The bridge to the Agent is up (the phone connection is on and the service runs).</summary>
    bool Connected { get; }

    /// <summary><c>mobile:enabled</c> — without it there is no bridge and no other device.</summary>
    bool FeatureEnabled { get; }

    /// <summary>The Agent answered <c>live.get</c> with "unsupported" (older than v0.34.0).</summary>
    bool LiveUnsupported { get; }

    Task<DeviceOverview?> OverviewAsync();

    /// <summary>The live view of <paramref name="target"/>; null when the bridge is down or the Agent is too old.</summary>
    Task<LiveView?> LiveAsync(string target);
}

/// <summary>The real source: <c>devices.overview</c> and <c>live.get</c> over the mobile bridge (M-46).</summary>
internal sealed class BridgeDashboardSource : IDashboardSource
{
    private bool? _enabled;
    private DateTime _enabledAt;

    public bool Connected => MobileHost.Client is { IsConnected: true };

    public bool FeatureEnabled
    {
        get
        {
            // The config file is read at most every 30 s; Settings may turn the feature on meanwhile.
            if (_enabled is null || DateTime.UtcNow - _enabledAt > TimeSpan.FromSeconds(30))
            {
                try { _enabled = MobileHost.IsEnabled(); } catch { _enabled = false; }
                _enabledAt = DateTime.UtcNow;
            }
            return _enabled.Value;
        }
    }

    public bool LiveUnsupported { get; private set; }

    public async Task<DeviceOverview?> OverviewAsync()
    {
        if (MobileHost.Client is not { IsConnected: true } client) return null;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpDevicesOverview);
            return (bool?)r["ok"] == true ? r["payload"]?.Deserialize<DeviceOverview>(MobileProtocol.Json) : null;
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException) { return null; }
    }

    public async Task<LiveView?> LiveAsync(string target)
    {
        if (MobileHost.Client is not { IsConnected: true } client) return null;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpLiveGet, new { target }, timeout: TimeSpan.FromSeconds(5));
            if ((bool?)r["ok"] == true)
            {
                LiveUnsupported = false;
                return r["payload"]?.Deserialize<LiveView>(MobileProtocol.Json);
            }
            LiveUnsupported = MobileBridge.StringOf(r["error"]) == MobileBridge.ErrorUnsupported;
            return null;
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException) { return null; }
    }
}
