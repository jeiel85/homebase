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
        services.AddSingleton(sp => new UnifiedPushOutboundChannel(
            sp.GetRequiredService<IMobileDeviceStore>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(UnifiedPushHttpClient),
            sp.GetRequiredService<TimeProvider>(),
            Environment.MachineName,
            sp.GetRequiredService<ILogger<UnifiedPushOutboundChannel>>()));
        services.AddSingleton<IOutboundChannel>(sp => sp.GetRequiredService<UnifiedPushOutboundChannel>());
        services.AddSingleton<IResourceSampler, WindowsResourceSampler>();
        services.AddSingleton<TailscaleAddressResolver>();
        services.AddSingleton<LanAddressResolver>();
        services.AddSingleton<LiveSnapshotHub>();
        services.AddSingleton<CloudflareTunnel>();
        // Hub for other devices (M-27, M-28): their state, relayed notifications, the inbox for linked PCs.
        services.AddSingleton<DeviceStatusBoard>();
        services.AddSingleton<NotificationInbox>();
        services.AddSingleton<SystemStatusProbe>();
        services.AddSingleton<TrayToastRelay>();
        services.AddSingleton<ITrayToaster>(sp => sp.GetRequiredService<TrayToastRelay>());
        services.AddSingleton(sp =>
        {
            // Null when Windows notification forwarding is off: the hub then lists devices but relays nothing.
            var forwarding = sp.GetService<NotificationForwardingService>();
            var probe = sp.GetRequiredService<SystemStatusProbe>();
            return new DeviceHub(
                sp.GetRequiredService<IMobileDeviceStore>(), sp.GetRequiredService<DeviceStatusBoard>(),
                sp.GetRequiredService<NotificationInbox>(), probe.Read,
                forwarding is null ? null : (origin, n) => forwarding.AcceptFromDeviceAsync(origin, n.App, n.Title, n.Body, n.Ts, n.Id),
                Environment.MachineName, SystemStatusProbe.AgentVersion, sp.GetRequiredService<ILogger<DeviceHub>>());
        });
        // Links to other PCs this one paired with as a device (M-29).
        services.AddSingleton(sp => new HubStore(MobileDeviceStore.DefaultDirectory, sp.GetRequiredService<ILogger<HubStore>>()));
        services.AddSingleton(sp =>
        {
            var probe = sp.GetRequiredService<SystemStatusProbe>();
            return new HubLinkService(
                sp.GetRequiredService<HubStore>(), sp.GetRequiredService<IMobileDeviceStore>(),
                new SystemStatusProbeAdapter(probe.Report), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<HubLinkService>>(), sp.GetService<NotificationForwardingService>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<HubLinkService>());
        services.AddSingleton(sp => new MobileApiServer(
            sp.GetRequiredService<LiveSnapshotHub>(), sp.GetRequiredService<PairingService>(),
            sp.GetRequiredService<IMobileDeviceStore>(), sp.GetRequiredService<TailscaleAddressResolver>(),
            sp.GetRequiredService<LanAddressResolver>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MobileOptions>>(),
            sp.GetRequiredService<ILogger<MobileApiServer>>(), sp.GetRequiredService<CloudflareTunnel>(),
            deviceHub: sp.GetRequiredService<DeviceHub>()));
        services.AddHostedService(sp => sp.GetRequiredService<MobileApiServer>());
        // Idle (returns at once) unless mobile:cloudflare:enabled.
        services.AddHostedService(sp => sp.GetRequiredService<CloudflareTunnel>());
        services.AddHostedService<MobileBridgeServer>();
        // New paths (Quick Tunnel address after a restart, new LAN IP) go to phones by push (M-23).
        services.AddHostedService(sp => new EndpointAnnouncer(
            sp.GetRequiredService<MobileApiServer>().Endpoints,
            sp.GetRequiredService<UnifiedPushOutboundChannel>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<EndpointAnnouncer>>()));
        return services;
    }
}
