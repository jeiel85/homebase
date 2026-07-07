using LocalOpsBot.Core.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace LocalOpsBot.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalOpsCore(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter, CommandRouter>();
        services.AddSingleton<ICommandHandler, PingCommandHandler>();
        services.AddSingleton<ICommandHandler, StatusCommandHandler>();
        services.AddSingleton<ICommandHandler, DiskCommandHandler>();
        services.AddSingleton<ICommandHandler, UptimeCommandHandler>();

        return services;
    }
}
