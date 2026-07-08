using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Core.Commands;

public sealed class DiagnosticsCommandHandler : ICommandHandler
{
    private readonly IStateStore _stateStore;
    private readonly IAlertStore _alertStore;
    private readonly IReadOnlyList<ProcessWatchConfig> _processWatches;
    private readonly IReadOnlyList<ServiceWatchConfig> _serviceWatches;
    private readonly IReadOnlyList<HttpEndpointConfig> _httpEndpoints;
    private readonly IReadOnlyList<TcpPortConfig> _tcpPorts;
    private readonly IConfiguration _config;

    public string CommandName => "diagnostics";
    public string Description => "Agent self-diagnostics";

    public DiagnosticsCommandHandler(
        IStateStore stateStore,
        IAlertStore alertStore,
        IEnumerable<ProcessWatchConfig> processWatches,
        IEnumerable<ServiceWatchConfig> serviceWatches,
        IEnumerable<HttpEndpointConfig> httpEndpoints,
        IEnumerable<TcpPortConfig> tcpPorts,
        IConfiguration config)
    {
        _stateStore = stateStore;
        _alertStore = alertStore;
        _processWatches = processWatches.ToList();
        _serviceWatches = serviceWatches.ToList();
        _httpEndpoints = httpEndpoints.ToList();
        _tcpPorts = tcpPorts.ToList();
        _config = config;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var version = Assembly.GetEntryAssembly()?
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                      ?? "unknown";

        string uptime;
        try
        {
            var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
            uptime = elapsed.TotalDays >= 1
                ? $"{(int)elapsed.TotalDays}d {elapsed.Hours}h {elapsed.Minutes}m"
                : $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        }
        catch { uptime = "unknown"; }

        string dbStatus;
        try { await _stateStore.GetAsync("telegram.last_update_offset", ct); dbStatus = "OK"; }
        catch (Exception ex) { dbStatus = $"FAIL ({ex.GetType().Name})"; }

        var muteStatus = "Off";
        try
        {
            var mutedUntil = await _stateStore.GetAsync("alert.muted_until", ct);
            if (DateTime.TryParse(mutedUntil, null, DateTimeStyles.RoundtripKind, out var until) && DateTime.UtcNow < until)
                muteStatus = $"until {until.ToLocalTime():HH:mm}";
        }
        catch { /* leave Off */ }

        int alerts24h;
        try
        {
            var recent = await _alertStore.GetRecentAsync(200, ct);
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            alerts24h = recent.Count(a => a.CreatedAt >= cutoff && a.Status == "Sent");
        }
        catch { alerts24h = -1; }

        var forwarding = _config.GetSection("notificationForwarding").GetValue<bool>("enabled") ? "On" : "Off";

        var lines = new List<string>
        {
            "<b>\U0001f9ea LocalOps Diagnostics</b>\n",
            $"Version: <code>{version}</code>",
            $"Agent uptime: <code>{uptime}</code>",
            $"Database: <code>{dbStatus}</code>",
            $"Mute: <code>{muteStatus}</code>",
            $"Watches: <code>{_processWatches.Count} process, {_serviceWatches.Count} service, {_httpEndpoints.Count} http, {_tcpPorts.Count} port</code>",
            $"Alerts sent (24h): <code>{(alerts24h < 0 ? "?" : alerts24h.ToString())}</code>",
            $"Notification forwarding: <code>{forwarding}</code>",
        };

        return new CommandResult(true, string.Join("\n", lines));
    }
}
