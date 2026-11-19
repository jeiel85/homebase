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
        // GPU, drives, temperatures, ping for the live view (M-34); the temperature collector is the one the advisor uses.
        services.AddSingleton<IHardwareSampler, WindowsHardwareSampler>();
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
                Environment.MachineName, SystemStatusProbe.AgentVersion, sp.GetRequiredService<ILogger<DeviceHub>>(),
                sp.GetRequiredService<TimeProvider>());
        });
        // Links to other PCs this one paired with as a device (M-29).
        services.AddSingleton(sp => new HubStore(MobileDeviceStore.DefaultDirectory, sp.GetRequiredService<ILogger<HubStore>>()));
        services.AddSingleton(sp =>
        {
            var probe = sp.GetRequiredService<SystemStatusProbe>();
            // Read when a presence goes out, long after both are built (the API server needs the group, which needs this).
            var api = new Lazy<MobileApiServer>(() => sp.GetRequiredService<MobileApiServer>());
            return new HubLinkService(
                sp.GetRequiredService<HubStore>(), sp.GetRequiredService<IMobileDeviceStore>(),
                new SystemStatusProbeAdapter(probe.Report), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<HubLinkService>>(), sp.GetService<NotificationForwardingService>(),
                finder: new HubFinder(
                    HubFinder.NetworkCandidates(sp.GetRequiredService<TailscaleAddressResolver>(), sp.GetRequiredService<LanAddressResolver>()),
                    sp.GetRequiredService<ILogger<HubFinder>>()),
                live: sp.GetRequiredService<LiveSnapshotHub>(),
                ownPaths: () => api.Value.Endpoints());
        });
        // The device group (M-47): links both ways, introductions, removals that reach every member.
        services.AddSingleton(sp => GroupTombstoneFile.Open(MobileDeviceStore.DefaultDirectory,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DeviceGroupService>>()));
        // The group's aliases (M-51) travel with the group answers.
        services.AddSingleton(sp => DeviceAliasFile.Open(MobileDeviceStore.DefaultDirectory,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DeviceGroupService>>()));
        services.AddSingleton(sp =>
        {
            var api = new Lazy<MobileApiServer>(() => sp.GetRequiredService<MobileApiServer>());
            return new DeviceGroupService(sp.GetRequiredService<HubLinkService>(), sp.GetRequiredService<IMobileDeviceStore>(),
                sp.GetRequiredService<DeviceStatusBoard>(), sp.GetRequiredService<GroupTombstones>(),
                () => api.Value.CreateOffer(introduction: true), Environment.MachineName,
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DeviceGroupService>>(),
                sp.GetRequiredService<DeviceAliasTable>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<DeviceGroupService>());
        // The Tray's device page (M-46): this PC, linked PCs and PCs linked here, live while it looks.
        services.AddSingleton(sp => new TrayLiveService(
            sp.GetRequiredService<LiveSnapshotHub>(), sp.GetRequiredService<IMobileDeviceStore>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<TrayLiveService>>(),
            sp.GetRequiredService<DeviceHub>(), sp.GetRequiredService<HubLinkService>()));
        services.AddHostedService(sp => sp.GetRequiredService<HubLinkService>());
        // Claude Code permission prompts waiting for a phone (M-48).
        services.AddSingleton(sp => new ApprovalBroker(sp.GetRequiredService<ILogger<ApprovalBroker>>(), sp.GetService<IOutboundRouter>(),
            devices: sp.GetRequiredService<IMobileDeviceStore>()));
        // Clipboard text sent by hand (M-62): in by the API, out by push or over a PC link, keys agreed by ECDH.
        services.AddSingleton<IClipKeyStore>(sp => new ClipKeyFile(MobileDeviceStore.DefaultDirectory,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ClipShareService>>()));
        services.AddSingleton(sp =>
        {
            var push = sp.GetRequiredService<UnifiedPushOutboundChannel>();
            return new ClipShareService(sp.GetRequiredService<IMobileDeviceStore>(), sp.GetRequiredService<IClipKeyStore>(),
                (target, plaintext, ct) => push.SendDirectAsync(target, plaintext, ClipProtocol.PushTtlSeconds, "high", ct),
                sp.GetRequiredService<HubLinkService>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ClipShareService>>());
        });
        services.AddSingleton(sp =>
        {
            var api = new MobileApiServer(
                sp.GetRequiredService<LiveSnapshotHub>(), sp.GetRequiredService<PairingService>(),
                sp.GetRequiredService<IMobileDeviceStore>(), sp.GetRequiredService<TailscaleAddressResolver>(),
                sp.GetRequiredService<LanAddressResolver>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MobileOptions>>(),
                sp.GetRequiredService<ILogger<MobileApiServer>>(), sp.GetRequiredService<CloudflareTunnel>(),
                deviceHub: sp.GetRequiredService<DeviceHub>(), approvals: sp.GetRequiredService<ApprovalBroker>(),
                advice: sp.GetService<LocalOpsBot.Core.Advisor.OnDemandAdvice>(), clips: sp.GetRequiredService<ClipShareService>());
            api.AttachGroup(sp.GetRequiredService<DeviceGroupService>());
            return api;
        });
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
