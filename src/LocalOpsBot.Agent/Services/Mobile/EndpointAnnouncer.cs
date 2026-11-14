using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Push;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Pushes this PC's paths to paired phones when a new one appears (DECISIONS M-23). A phone only
/// fetches <c>/api/v1/endpoints</c> after it has connected, so without this a restart that changes the
/// Quick Tunnel address (or a new LAN IP) strands a phone that can reach the PC only that way until
/// it is paired again.
/// <list type="bullet">
/// <item>Announces once the list has been the same for two polls (start-up settles: the tunnel comes up
/// a few seconds after the listeners).</item>
/// <item>Only a path not announced before triggers a push — a dropped path doesn't, so a flapping Wi-Fi
/// adapter doesn't burn the push quota (ntfy.sh: 250/day per phone).</item>
/// <item>Tracked per phone; a phone that didn't accept the push is retried after <see cref="RetryAfter"/>.
/// A phone that registers push later gets one push even though it has just fetched the list itself.</item>
/// </list>
/// </summary>
public sealed class EndpointAnnouncer : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly Func<IReadOnlyList<MobileEndpoint>> _endpoints;
    private readonly UnifiedPushOutboundChannel _push;
    private readonly TimeProvider _time;
    private readonly ILogger<EndpointAnnouncer> _logger;

    private HashSet<MobileEndpoint>? _previous;
    /// <summary>Device id → the path list that phone last accepted (memory only: a restart announces again).</summary>
    private readonly Dictionary<string, HashSet<MobileEndpoint>> _announced = new(StringComparer.Ordinal);
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public EndpointAnnouncer(
        Func<IReadOnlyList<MobileEndpoint>> endpoints,
        UnifiedPushOutboundChannel push,
        TimeProvider time,
        ILogger<EndpointAnnouncer> logger)
    {
        _endpoints = endpoints;
        _push = push;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, _time, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let this stop the host (and with it Telegram monitoring).
                _logger.LogError(ex, "Endpoint announcement failed");
                _nextAttempt = _time.GetUtcNow() + RetryAfter;
            }
        }
    }

    /// <summary>One poll. Returns whether a push was attempted.</summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        var current = _endpoints().ToHashSet();
        var stable = _previous is not null && _previous.SetEquals(current);
        _previous = current;
        if (!stable || current.Count == 0) return false;

        // Per phone: one that missed the push (rate limit, distributor offline) is retried on its own,
        // without re-sending to the phones that already have the list.
        var targets = _push.EndpointTargetIds();
        foreach (var gone in _announced.Keys.Except(targets).ToList()) _announced.Remove(gone);
        var due = targets.Where(id => !_announced.TryGetValue(id, out var told) || !current.IsSubsetOf(told)).ToList();
        if (due.Count == 0 || _time.GetUtcNow() < _nextAttempt) return false;

        var delivered = await _push.SendEndpointsAsync(current.ToList(), due, ct);
        foreach (var id in delivered) _announced[id] = current;
        if (delivered.Count > 0)
            _logger.LogInformation("Sent the current mobile paths to {Count} phone(s): {Hosts}.",
                delivered.Count, string.Join(", ", current.Select(e => $"{e.Transport}:{e.Host}")));

        var missed = due.Count - delivered.Count;
        if (missed > 0)
        {
            _nextAttempt = _time.GetUtcNow() + RetryAfter;
            _logger.LogWarning("{Count} phone(s) did not accept the mobile path update; retrying in {Minutes} min.",
                missed, (int)RetryAfter.TotalMinutes);
        }
        else
        {
            _nextAttempt = DateTimeOffset.MinValue;
        }
        return true;
    }
}
