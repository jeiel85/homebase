using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LocalOpsBot.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalOpsCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ICommandRouter, CommandRouter>();
        services.AddSingleton<ICommandHandler, PingCommandHandler>();
        services.AddSingleton<ICommandHandler, StatusCommandHandler>();
        services.AddSingleton<ICommandHandler, DiskCommandHandler>();
        services.AddSingleton<ICommandHandler, UptimeCommandHandler>();
        services.AddSingleton<ICommandHandler, ProcessCommandHandler>();
        services.AddSingleton<ICommandHandler, ServicesCommandHandler>();
        services.AddSingleton<ICommandHandler, EventsCommandHandler>();
        services.AddSingleton<ICommandHandler, MuteCommandHandler>();
        services.AddSingleton<ICommandHandler, UnmuteCommandHandler>();
        services.AddSingleton<ICommandHandler, AlertsCommandHandler>();
        services.AddSingleton<ICommandHandler, DevCommandHandler>();
        services.AddSingleton<ICommandHandler, PortsCommandHandler>();
        services.AddSingleton<ICommandHandler, HelpCommandHandler>();
        services.AddSingleton<ICommandHandler, WatchCommandHandler>();
        services.AddSingleton<ICommandHandler, PolicyCommandHandler>();
        services.AddSingleton<ICommandHandler, UpdateCommandHandler>();

        var alertingOpts = config.GetSection("alerting").Get<AlertingOptions>() ?? new AlertingOptions();
        services.AddSingleton(alertingOpts);
        services.AddSingleton<IAlertPolicy, AlertPolicy>();

        var processWatches = config.GetSection("processWatches").Get<ProcessWatchConfig[]>() ?? [];
        services.AddSingleton<IReadOnlyList<ProcessWatchConfig>>(processWatches);
        services.AddSingleton(processWatches.AsEnumerable());

        var serviceWatches = config.GetSection("serviceWatches").Get<ServiceWatchConfig[]>() ?? [];
        services.AddSingleton<IReadOnlyList<ServiceWatchConfig>>(serviceWatches);
        services.AddSingleton(serviceWatches.AsEnumerable());

        var eventLogOpts = config.GetSection("eventLog").Get<EventLogOptions>() ?? new EventLogOptions();
        services.AddSingleton(eventLogOpts);

        var devSection = config.GetSection("developerMonitors");
        var httpEndpoints = devSection.GetSection("httpEndpoints").Get<HttpEndpointConfig[]>() ?? [];
        services.AddSingleton<IReadOnlyList<HttpEndpointConfig>>(httpEndpoints);
        services.AddSingleton(httpEndpoints.AsEnumerable());
        var tcpPorts = devSection.GetSection("tcpPorts").Get<TcpPortConfig[]>() ?? [];
        services.AddSingleton<IReadOnlyList<TcpPortConfig>>(tcpPorts);
        services.AddSingleton(tcpPorts.AsEnumerable());

        return services;
    }
}
