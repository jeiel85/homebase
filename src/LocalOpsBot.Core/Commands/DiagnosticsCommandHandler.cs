using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Localization;
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
    private readonly ITelegramPollStatus _pollStatus;
    private readonly TemperatureOptions _temperatureOptions;
    private readonly ITemperatureCollector _temperature;

    public string CommandName => "diagnostics";
    public string Description => "Agent self-diagnostics";

    public DiagnosticsCommandHandler(
        IStateStore stateStore,
        IAlertStore alertStore,
        IEnumerable<ProcessWatchConfig> processWatches,
        IEnumerable<ServiceWatchConfig> serviceWatches,
        IEnumerable<HttpEndpointConfig> httpEndpoints,
        IEnumerable<TcpPortConfig> tcpPorts,
        IConfiguration config,
        ITelegramPollStatus pollStatus,
        TemperatureOptions temperatureOptions,
        ITemperatureCollector temperature)
    {
        _stateStore = stateStore;
        _alertStore = alertStore;
        _processWatches = processWatches.ToList();
        _serviceWatches = serviceWatches.ToList();
        _httpEndpoints = httpEndpoints.ToList();
        _tcpPorts = tcpPorts.ToList();
        _config = config;
        _pollStatus = pollStatus;
        _temperatureOptions = temperatureOptions;
        _temperature = temperature;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var version = Assembly.GetEntryAssembly()?
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                      ?? Strings.Unknown;

        string uptime;
        try
        {
            var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
            uptime = elapsed.TotalDays >= 1
                ? $"{(int)elapsed.TotalDays}d {elapsed.Hours}h {elapsed.Minutes}m"
                : $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        }
        catch { uptime = Strings.Unknown; }

        string dbStatus;
        try { await _stateStore.GetAsync("telegram.last_update_offset", ct); dbStatus = Strings.Ok; }
        catch (Exception ex) { dbStatus = $"FAIL ({ex.GetType().Name})"; }

        var muteStatus = Strings.Off;
        try
        {
            var mutedUntil = await _stateStore.GetAsync("alert.muted_until", ct);
            if (DateTime.TryParse(mutedUntil, null, DateTimeStyles.RoundtripKind, out var until) && DateTime.UtcNow < until)
                muteStatus = Strings.MuteUntil($"{until.ToLocalTime():HH:mm}");
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

        var forwarding = _config.GetSection("notificationForwarding").GetValue<bool>("enabled") ? Strings.On : Strings.Off;

        // Temperature backend + live sensor count — makes "why are temps blank?" self-diagnosable
        // (e.g. "Wmi, 0 sensors" on a desktop that doesn't expose ACPI zones → opt into full sensors).
        string temperature;
        if (!_temperatureOptions.Enabled)
        {
            temperature = Strings.Off;
        }
        else
        {
            try
            {
                var tempResult = await _temperature.CollectAsync(ct);
                var count = tempResult.Success ? tempResult.Snapshot?.Sensors.Count ?? 0 : 0;
                temperature = $"{_temperatureOptions.Source}, {count} sensor{(count == 1 ? "" : "s")}";
            }
            catch (Exception ex)
            {
                temperature = $"{_temperatureOptions.Source} (error: {ex.GetType().Name})";
            }
        }

        string lastPoll;
        var lastPollUtc = _pollStatus.LastSuccessfulPollUtc;
        if (lastPollUtc is null)
        {
            lastPoll = Strings.Never;
        }
        else
        {
            var age = DateTimeOffset.UtcNow - lastPollUtc.Value;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            lastPoll = $"{lastPollUtc.Value.ToLocalTime():HH:mm:ss} ({Strings.TimeAgo(FormatAge(age))})";
        }
        var pollFailures = _pollStatus.ConsecutiveFailures;

        var lines = new List<string>
        {
            $"<b>\U0001f9ea {Strings.DiagnosticsTitle}</b>\n",
            $"{Strings.VersionLabel}: <code>{version}</code>",
            $"{Strings.AgentUptimeLabel}: <code>{uptime}</code>",
            $"{Strings.DatabaseLabel}: <code>{dbStatus}</code>",
            $"{Strings.LastPollLabel}: <code>{lastPoll}</code>",
            $"{Strings.PollFailuresLabel}: <code>{pollFailures}</code>",
            $"{Strings.MuteLabel}: <code>{muteStatus}</code>",
            $"{Strings.WatchesLabel}: <code>{Strings.WatchesSummary(_processWatches.Count, _serviceWatches.Count, _httpEndpoints.Count, _tcpPorts.Count)}</code>",
            $"{Strings.AlertsSent24hLabel}: <code>{(alerts24h < 0 ? "?" : alerts24h.ToString())}</code>",
            $"{Strings.ForwardingLabel}: <code>{forwarding}</code>",
            $"{Strings.TemperatureLabel}: <code>{temperature}</code>",
        };

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string FormatAge(TimeSpan a) =>
        a.TotalSeconds < 60 ? $"{(int)a.TotalSeconds}s"
        : a.TotalMinutes < 60 ? $"{(int)a.TotalMinutes}m"
        : a.TotalHours < 24 ? $"{(int)a.TotalHours}h"
        : $"{(int)a.TotalDays}d";
}
