using System.Runtime.Versioning;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

[SupportedOSPlatform("windows")]
public static class MobileServiceCollectionExtensions
{
    private const string UnifiedPushHttpClient = "UnifiedPush";

    /// <summary>
    /// Registers the mobile monitoring feature. With <c>mobile:enabled=false</c> (default) nothing
    /// is registered except the firewall clean-up, so the feature adds no threads, sockets or pipes.
    /// </summary>
    public static IServiceCollection AddHomebaseMobile(this IServiceCollection services, IConfiguration configuration, ILogger startupLogger)
    {
        var section = configuration.GetSection(MobileOptions.SectionName);
        services.Configure<MobileOptions>(section);
        var options = section.Get<MobileOptions>() ?? new MobileOptions();

        MobileFirewall.Apply(options, startupLogger);
        if (!options.Enabled) return services;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMobileDeviceStore>(sp => new MobileDeviceStore(
            MobileDeviceStore.DefaultDirectory, sp.GetRequiredService<TimeProvider>(), restrictAcl: true,
            sp.GetRequiredService<ILogger<MobileDeviceStore>>()));
        services.AddSingleton<PairingService>();
        // Encrypted UnifiedPush to paired phones that registered an endpoint (DECISIONS M-19).
        // The router prefers it over Telegram; it never subscribes to the live hub (M-13).
        services.AddHttpClient(UnifiedPushHttpClient, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IOutboundChannel>(sp => new UnifiedPushOutboundChannel(
            sp.GetRequiredService<IMobileDeviceStore>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(UnifiedPushHttpClient),
            sp.GetRequiredService<TimeProvider>(),
            Environment.MachineName,
            sp.GetRequiredService<ILogger<UnifiedPushOutboundChannel>>()));
        services.AddSingleton<IResourceSampler, WindowsResourceSampler>();
        services.AddSingleton<TailscaleAddressResolver>();
        services.AddSingleton<LanAddressResolver>();
        services.AddSingleton<LiveSnapshotHub>();
        services.AddSingleton<CloudflareTunnel>();
        services.AddSingleton(sp => new MobileApiServer(
            sp.GetRequiredService<LiveSnapshotHub>(), sp.GetRequiredService<PairingService>(),
            sp.GetRequiredService<IMobileDeviceStore>(), sp.GetRequiredService<TailscaleAddressResolver>(),
            sp.GetRequiredService<LanAddressResolver>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MobileOptions>>(),
            sp.GetRequiredService<ILogger<MobileApiServer>>(), sp.GetRequiredService<CloudflareTunnel>()));
        services.AddHostedService(sp => sp.GetRequiredService<MobileApiServer>());
        // Idle (returns at once) unless mobile:cloudflare:enabled.
        services.AddHostedService(sp => sp.GetRequiredService<CloudflareTunnel>());
        services.AddHostedService<MobileBridgeServer>();
        return services;
    }
}
