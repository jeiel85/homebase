using System.Runtime.Versioning;
using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Core.Updates;
using LocalOpsBot.Infrastructure.Commands;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Infrastructure.Notifications;
using LocalOpsBot.Infrastructure.Telegram;
using LocalOpsBot.Infrastructure.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    // Local-LLM advisor client (Ollama HTTP). LlmAdvisorOptions is bound in AddLocalOpsCore;
    // this just wires the typed HttpClient so the endpoint/model can be resolved at call time.
    public static IServiceCollection AddLocalOpsLlm(this IServiceCollection services)
    {
        services.AddHttpClient<ILlmClient, OllamaLlmClient>();
        return services;
    }

    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddLocalOpsNotificationForwarding(
        this IServiceCollection services, IConfiguration config)
    {
        var forwardingSection = config.GetSection("notificationForwarding");
        var enabled = forwardingSection.GetValue<bool>("enabled");
        if (!enabled) return services;

        var mode = forwardingSection.GetValue<string>("mode") ?? "BlockList";
        var allowApps = forwardingSection.GetSection("allowApps").Get<string[]>() ?? [];
        var blockApps = forwardingSection.GetSection("blockApps").Get<string[]>() ?? [];
        var maskingEnabled = forwardingSection.GetValue<bool>("maskingEnabled");
        var maskPatterns = forwardingSection.GetSection("maskPatterns").Get<string[]>() ?? GetDefaultMaskPatterns();

        services.AddSingleton<ITextMasker>(new RegexTextMasker(maskPatterns));
        services.AddSingleton<INotificationFilter>(sp =>
            new NotificationFilter(mode, allowApps, blockApps, sp.GetRequiredService<ITextMasker>(), maskingEnabled));

        return services;
    }

    private static string[] GetDefaultMaskPatterns() => new[]
    {
        "(?<!\\d)\\d{6}(?!\\d)",
        "(?i)(password|passwd|pwd)\\s*[:=]\\s*\\S+",
        "(?i)bearer\\s+[a-z0-9._\\-]+",
        "(?i)(token|secret|api[_-]?key)\\s*[:=]\\s*\\S+"
    };

    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddLocalOpsWindowsCollectors(
        this IServiceCollection services)
    {
        services.AddSingleton<IHostInfoProvider, HostInfoProvider>();
        services.AddSingleton<ISystemMetricsCollector, WindowsSystemMetricsCollector>();
        services.AddSingleton<IDiskCollector, WindowsDiskCollector>();
        services.AddSingleton<INetworkStatusChecker, WindowsNetworkStatusChecker>();
        services.AddSingleton<IProcessCollector, WindowsProcessCollector>();
        services.AddSingleton<IWindowsServiceCollector, WindowsServiceCollector>();
        services.AddSingleton<IEventLogWatcher, WindowsEventLogWatcher>();
        // Pick the temperature backend from config. The default (WMI) loads no kernel driver; the
        // LibreHardware backend is opt-in and loads WinRing0, which antivirus flags. Singleton so
        // the LHM kernel driver opens at most once; the container disposes it on shutdown.
        services.AddSingleton<ITemperatureCollector>(sp =>
        {
            var options = sp.GetService<TemperatureOptions>() ?? new TemperatureOptions();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return options.Source == TemperatureSource.LibreHardware
                ? new LibreHardwareTemperatureCollector(
                    options, loggerFactory?.CreateLogger<LibreHardwareTemperatureCollector>())
                : new WmiTemperatureCollector(
                    options, loggerFactory?.CreateLogger<WmiTemperatureCollector>());
        });

        services.AddSingleton<IHttpEndpointMonitor, HttpEndpointMonitor>();
        services.AddSingleton<ITcpPortMonitor, TcpPortMonitor>();

        return services;
    }

    public static IServiceCollection AddLocalOpsDevMonitor(
        this IServiceCollection services)
    {
        services.AddHttpClient("DevMonitor", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }

    public static IServiceCollection AddLocalOpsUpdates(
        this IServiceCollection services)
    {
        services.AddHttpClient<UpdateService>("GitHub", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Homebase/0.1");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        return services;
    }
}
