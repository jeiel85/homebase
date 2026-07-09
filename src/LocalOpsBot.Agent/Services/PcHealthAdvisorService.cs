using System.Runtime.Versioning;
using LocalOpsBot.Core.Advisor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Periodically runs the PC health monitor. When a threshold breach persists, the monitor asks the
/// local LLM for advice and dispatches it to Telegram. This service owns only the timer loop; all
/// decisions (thresholds, sustained-breach streak, cooldown) live in <see cref="PcHealthMonitor"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PcHealthAdvisorService : BackgroundService
{
    private readonly PcHealthMonitor _monitor;
    private readonly AdvisorAlertOptions _options;
    private readonly ILogger<PcHealthAdvisorService> _logger;

    public PcHealthAdvisorService(
        PcHealthMonitor monitor,
        AdvisorAlertOptions options,
        ILogger<PcHealthAdvisorService> logger)
    {
        _monitor = monitor;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Health advisor: disabled (advisorAlerts.enabled is false), skipping");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.IntervalSeconds));
        _logger.LogInformation(
            "Health advisor started: every {Seconds}s, cooldown {Cooldown}m (cpu={Cpu}% mem={Mem}% disk={Disk}% temp={Temp}C)",
            (int)interval.TotalSeconds, _options.CooldownMinutes,
            _options.CpuPercent, _options.MemoryPercent, _options.DiskPercent, _options.TempCelsius);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                LogOutcome(await _monitor.PollOnceAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health advisor iteration failed");
            }
        }
    }

    private void LogOutcome(PollResult result)
    {
        switch (result.Outcome)
        {
            case AdviseOutcome.Advised:
                _logger.LogInformation("Health advice sent (triggered by: {Trigger})", result.Detail);
                break;
            case AdviseOutcome.AdviceFailed:
                _logger.LogWarning("Health breach detected but advice unavailable: {Error}", result.Detail);
                break;
            case AdviseOutcome.InCooldown:
                _logger.LogDebug("Health breach within cooldown; advice suppressed");
                break;
            default:
                // NoBreach / BelowStreak / Disabled: not worth a log line every poll.
                break;
        }
    }
}
