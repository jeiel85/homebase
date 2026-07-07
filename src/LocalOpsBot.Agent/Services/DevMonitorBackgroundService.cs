using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class DevMonitorBackgroundService : BackgroundService
{
    private readonly IHttpEndpointMonitor _httpMonitor;
    private readonly ITcpPortMonitor _tcpMonitor;
    private readonly IReadOnlyList<HttpEndpointConfig> _endpoints;
    private readonly IReadOnlyList<TcpPortConfig> _ports;
    private readonly ILogger<DevMonitorBackgroundService> _logger;

    public DevMonitorBackgroundService(
        IHttpEndpointMonitor httpMonitor,
        ITcpPortMonitor tcpMonitor,
        IEnumerable<HttpEndpointConfig> endpoints,
        IEnumerable<TcpPortConfig> ports,
        ILogger<DevMonitorBackgroundService> logger)
    {
        _httpMonitor = httpMonitor;
        _tcpMonitor = tcpMonitor;
        _endpoints = endpoints.ToList();
        _ports = ports.ToList();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_endpoints.Count == 0 && _ports.Count == 0)
        {
            _logger.LogInformation("DevMonitor: no endpoints or ports configured, skipping");
            return;
        }

        _logger.LogInformation("DevMonitor started: {EndpointCount} endpoint(s), {PortCount} port(s)",
            _endpoints.Count, _ports.Count);

        var interval = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                foreach (var ep in _endpoints)
                {
                    var result = await _httpMonitor.CheckAsync(ep, ct);
                    if (!result.Success)
                        _logger.LogWarning("Endpoint '{Name}' is down: {Error} ({Url})",
                            result.Name, result.Error, result.Url);
                    else
                        _logger.LogDebug("Endpoint '{Name}' OK ({StatusCode}, {ResponseTimeMs}ms)",
                            result.Name, result.StatusCode, result.ResponseTimeMs);
                }

                foreach (var p in _ports)
                {
                    var result = await _tcpMonitor.CheckAsync(p, ct);
                    if (!result.Open)
                        _logger.LogWarning("Port '{Name}' ({Host}:{Port}) is closed: {Error}",
                            result.Name, result.Host, result.Port, result.Error);
                    else
                        _logger.LogDebug("Port '{Name}' open ({ResponseTimeMs}ms)", result.Name, result.ResponseTimeMs);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DevMonitor iteration failed");
            }
        }
    }
}
