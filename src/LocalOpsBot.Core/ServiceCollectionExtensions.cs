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

        var processWatches = config.GetSection("processWatches").Get<ProcessWatchConfig[]>() ?? [];
        services.AddSingleton<IReadOnlyList<ProcessWatchConfig>>(processWatches);
        services.AddSingleton(processWatches.AsEnumerable());

        var serviceWatches = config.GetSection("serviceWatches").Get<ServiceWatchConfig[]>() ?? [];
        services.AddSingleton<IReadOnlyList<ServiceWatchConfig>>(serviceWatches);
        services.AddSingleton(serviceWatches.AsEnumerable());

        return services;
    }
}
