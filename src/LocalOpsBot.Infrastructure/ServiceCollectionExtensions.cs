using System.Runtime.Versioning;
using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Commands;
using LocalOpsBot.Infrastructure.Telegram;
using LocalOpsBot.Infrastructure.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LocalOpsBot.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalOpsTelegram(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<TelegramOptions>()
            .Bind(config.GetSection("telegram"));

        services.AddHttpClient<ITelegramClient, TelegramClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(35);
        });

        services.AddSingleton<IChatAuthorizationPolicy, AllowedChatPolicy>();

        return services;
    }

    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddLocalOpsWindowsCollectors(
        this IServiceCollection services)
    {
        services.AddSingleton<ISystemMetricsCollector, WindowsSystemMetricsCollector>();
        services.AddSingleton<IDiskCollector, WindowsDiskCollector>();
        services.AddSingleton<INetworkStatusChecker, WindowsNetworkStatusChecker>();
        services.AddSingleton<IProcessCollector, WindowsProcessCollector>();
        services.AddSingleton<IWindowsServiceCollector, WindowsServiceCollector>();
        services.AddSingleton<IEventLogWatcher, WindowsEventLogWatcher>();

        return services;
    }
}
