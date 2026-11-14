using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Keeps this PC linked to the hub PCs it paired with as a device (DECISIONS M-29). Per hub:
/// <list type="bullet">
/// <item>every 30 s, this PC's state (and its phones' push hashes) → <c>POST /api/v1/presence</c>, which
/// answers with the hub's device list for the Tray;</item>
/// <item>this PC's own Windows notifications → <c>POST /api/v1/notifications</c>;</item>
/// <item>a long poll of <c>GET /api/v1/inbox</c>; what arrives is shown here and pushed to this PC's
/// phones, except phones the hub already reaches (M-28) — and never sent on again;</item>
/// <item>every 10 minutes, the hub's current paths (<c>GET /api/v1/endpoints</c>) replace the stored ones.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HubLinkService : BackgroundService
{
    public static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan EndpointsInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RejectedRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InboxTimeout = DeviceHub.InboxWait + TimeSpan.FromSeconds(15);

    private readonly HubStore _store;
    private readonly IMobileDeviceStore _devices;
    private readonly SystemStatusProbeAdapter _status;
    private readonly NotificationForwardingService? _forwarding;
    private readonly TimeProvider _time;
    private readonly ILogger<HubLinkService> _logger;
    private readonly bool _allowLoopback;
    private readonly ConcurrentDictionary<string, Link> _links = new(StringComparer.Ordinal);
    private CancellationToken _stopping;
    private readonly HubFinder? _finder;

    private sealed class Link(LinkedHub hub, HubClient client, CancellationTokenSource cts)
    {
        public LinkedHub Hub { get; set; } = hub;
        public HubClient Client { get; } = client;
        public CancellationTokenSource Cts { get; } = cts;
        public Channel<DeviceNotification> Outbox { get; } = Channel.CreateBounded<DeviceNotification>(
            new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public DevicesResponse? LastDevices { get; set; }
        public DateTimeOffset? LastContactAt { get; set; }
        public bool Online { get; set; }
        public string? Error { get; set; }
        public IReadOnlyCollection<string> HubPushHashes => LastDevices?.PushHashes ?? (IReadOnlyCollection<string>)Array.Empty<string>();
    }

    public HubLinkService(
        HubStore store,
        IMobileDeviceStore devices,
        SystemStatusProbeAdapter status,
        TimeProvider time,
        ILogger<HubLinkService> logger,
        NotificationForwardingService? forwarding = null,
        bool allowLoopback = false,
        HubFinder? finder = null)
    {
        _finder = finder;
        _store = store;
        _devices = devices;
        _status = status;
        _time = time;
        _logger = logger;
        _forwarding = forwarding;
        _allowLoopback = allowLoopback;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        if (_forwarding is not null) _forwarding.LocalAccepted += OnLocalNotification;
        foreach (var hub in _store.List()) Start(hub);
        stoppingToken.Register(() =>
        {
            if (_forwarding is not null) _forwarding.LocalAccepted -= OnLocalNotification;
            foreach (var link in _links.Values) link.Cts.Cancel();
        });
        return Task.CompletedTask;
    }

    /// <summary>What the Tray's device list shows for the linked PCs.</summary>
    public IReadOnlyList<LinkedHubView> Views() => _links.Values
        .Select(l => new LinkedHubView(l.Hub.PcId, l.LastDevices?.Hub.PcName ?? l.Hub.PcName, l.Online, l.LastContactAt,
            l.LastDevices?.Hub.Status, l.LastDevices?.Devices ?? (IReadOnlyList<DeviceEntry>)Array.Empty<DeviceEntry>(), l.Error,
            l.Hub.DeviceId))
        .OrderBy(v => v.PcName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Pairs with the PC behind a pairing link (the other Tray's QR link) and starts the link.</summary>
    /// <summary>
    /// Pairs with the PC behind a pairing link or a 24-word connection code (DECISIONS M-29, M-30)
    /// and starts the link.
    /// </summary>
    public async Task<HubLinkResult> LinkAsync(string link, CancellationToken ct)
    {
        if (ConnectionCode.TryDecode(link) is { } code) return await LinkWithCodeAsync(code, ct);
        if (LooksLikeWords(link))
            return new HubLinkResult(false, null, "invalid_code",
                "연결 코드가 맞지 않습니다. 24단어를 빠짐없이, 순서대로 붙여 넣었는지 확인하세요.");
        var json = PairingLink.DecodeJson(link.Trim()) ?? (link.TrimStart().StartsWith('{') ? link.Trim() : null);
        PairingOffer? offer = null;
        if (json is not null)
        {
            try { offer = JsonSerializer.Deserialize<PairingOffer>(json, MobileProtocol.Json); }
            catch (JsonException) { }
        }
        if (offer is null || string.IsNullOrEmpty(offer.Code) || offer.Endpoints is not { Count: > 0 })
            return new HubLinkResult(false, null, "invalid_link", "Homebase 페어링 링크가 아닙니다.");
        if (offer.PcId == _devices.PcId)
            return new HubLinkResult(false, offer.PcName, "self", "이 PC 자신의 링크입니다. 다른 PC의 링크를 붙여 넣으세요.");
        return await LinkWithOfferAsync(offer, ct);
    }

    /// <summary>Several plain words and no link or JSON: meant as a code, so say what is wrong with it.</summary>
    private static bool LooksLikeWords(string text)
    {
        var t = text.Trim();
        return !t.Contains("://") && !t.StartsWith('{') && t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 6;
    }

    private async Task<HubLinkResult> LinkWithCodeAsync(ConnectionCodeData code, CancellationToken ct)
    {
        if (code.MatchesPcId(_devices.PcId))
            return new HubLinkResult(false, null, "self", "이 PC 자신의 연결 코드입니다. 다른 PC에서 받은 코드를 붙여 넣으세요.");
        if (_finder is null) return new HubLinkResult(false, null, "off", "연결 코드를 쓸 수 없습니다.");
        var found = await _finder.FindAsync(code, ct);
        if (found is null)
            return new HubLinkResult(false, null, "not_found",
                "같은 네트워크(LAN)나 Tailscale에서 그 PC를 찾지 못했습니다. 두 PC가 같은 Wi-Fi 또는 같은 tailnet인지, " +
                "코드가 5분 안의 것인지 확인하세요. 밖에서 Cloudflare로만 닿는 PC는 '링크 복사'의 링크를 쓰세요.");
        _logger.LogInformation("Connection code: found {Name} via {Transport} {Host}.", found.Info.PcName, found.Endpoint.Transport, found.Endpoint.Host);
        var offer = new PairingOffer(1, found.Info.PcId, found.Info.PcName, Array.Empty<string>(), found.Endpoint.Port,
            code.PairingCode, _time.GetUtcNow() + PairingService.CodeLifetime, new[] { found.Endpoint });
        return await LinkWithOfferAsync(offer, ct);
    }

    private async Task<HubLinkResult> LinkWithOfferAsync(PairingOffer offer, CancellationToken ct)
    {
        var client = new HubClient(_allowLoopback);
        var reply = await client.PostAsync(offer.Endpoints, "/api/v1/pair",
            new PairRequest(offer.Code, Environment.MachineName, "Windows", DeviceKind.Pc), null, CallTimeout, ct);
        if (reply.Status == 0)
        {
            client.Dispose();
            return new HubLinkResult(false, offer.PcName, "unreachable",
                $"{offer.PcName}에 닿지 않습니다. 같은 네트워크(LAN·Tailscale)인지 확인하세요. ({reply.Error})");
        }
        var paired = reply.Ok ? reply.Read<PairResponse>() : null;
        if (paired is null)
        {
            client.Dispose();
            var message = reply.ErrorCode switch
            {
                "code_expired" => "링크가 만료됐습니다. 상대 PC에서 새 코드를 만드세요.",
                "invalid_code" => "이미 쓴 링크이거나 새 코드로 바뀌었습니다. 상대 PC에서 새 코드를 만드세요.",
                "locked" => "잘못된 시도가 많아 잠시 잠겼습니다. 몇 분 뒤 다시 하세요.",
                _ => $"상대 PC가 거절했습니다 (HTTP {reply.Status}).",
            };
            return new HubLinkResult(false, offer.PcName, "rejected", message);
        }

        var endpoints = offer.Endpoints;
        var fresh = await client.GetAsync(endpoints, "/api/v1/endpoints", paired.Token, CallTimeout, ct);
        if (fresh.Ok && fresh.Read<EndpointsResponse>() is { Endpoints.Count: > 0 } e) endpoints = e.Endpoints;
        client.Dispose();

        if (_links.TryRemove(paired.PcId, out var old))
        {
            old.Cts.Cancel();
            _ = UnpairQuietlyAsync(old.Hub);
        }
        var hub = new LinkedHub(paired.PcId, paired.PcName, paired.DeviceId, paired.Token, endpoints.ToList(), _time.GetUtcNow());
        _store.Upsert(hub);
        Start(hub);
        _logger.LogInformation("Linked to {Name} ({PcId}) as device {DeviceId}.", paired.PcName, paired.PcId, paired.DeviceId);
        return new HubLinkResult(true, paired.PcName, null, null);
    }

    public async Task<bool> UnlinkAsync(string pcId)
    {
        var hub = _store.List().FirstOrDefault(h => h.PcId == pcId);
        if (_links.TryRemove(pcId, out var link)) link.Cts.Cancel();
        if (hub is null) return false;
        await UnpairQuietlyAsync(hub);
        _store.Remove(pcId);
        _logger.LogInformation("Unlinked from {Name} ({PcId}).", hub.PcName, hub.PcId);
        return true;
    }

    private async Task UnpairQuietlyAsync(LinkedHub hub)
    {
        using var client = new HubClient(_allowLoopback);
        try
        {
            var r = await client.DeleteAsync(hub.Endpoints, "/api/v1/device", hub.Token, TimeSpan.FromSeconds(8), CancellationToken.None);
            if (!r.Ok) _logger.LogInformation("{Name} did not confirm the unpair ({Status}); it stays listed there until removed.", hub.PcName, r.Status);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Unpair call to {Name} failed", hub.PcName); }
    }

    private void Start(LinkedHub hub)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        var link = new Link(hub, new HubClient(_allowLoopback), cts);
        _links[hub.PcId] = link;
        var loops = new[]
        {
            Task.Run(() => RunPresenceAsync(link, cts.Token), CancellationToken.None),
            Task.Run(() => RunInboxAsync(link, cts.Token), CancellationToken.None),
            Task.Run(() => RunOutboxAsync(link, cts.Token), CancellationToken.None),
        };
        // Unlinked, re-linked or stopping: release the sockets once all three loops have ended.
        _ = Task.WhenAll(loops).ContinueWith(t =>
        {
            if (t.Exception is { } ex) _logger.LogWarning(ex.GetBaseException(), "Link to {Name} ended with an error", hub.PcName);
            link.Client.Dispose();
        }, TaskScheduler.Default);
    }

    private IReadOnlyList<string> OwnPushHashes() =>
        _devices.PushTargets().Select(t => PushHash.Of(t.Subscription.Endpoint)).Distinct().ToList();

    private async Task RunPresenceAsync(Link link, CancellationToken ct)
    {
        var endpointsAt = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var reply = await link.Client.PostAsync(link.Hub.Endpoints, "/api/v1/presence",
                    _status.Report(OwnPushHashes()), link.Hub.Token, CallTimeout, ct);
                var wait = PresenceInterval;
                if (reply.Ok && reply.Read<DevicesResponse>() is { } devices)
                {
                    if (!link.Online) _logger.LogInformation("Linked PC {Name} reachable via {Transport}.", link.Hub.PcName, reply.Via?.Transport);
                    link.LastDevices = devices;
                    link.LastContactAt = _time.GetUtcNow();
                    link.Online = true;
                    link.Error = null;
                    if (_time.GetUtcNow() - endpointsAt >= EndpointsInterval)
                    {
                        var e = await link.Client.GetAsync(link.Hub.Endpoints, "/api/v1/endpoints", link.Hub.Token, CallTimeout, ct);
                        if (e.Ok && e.Read<EndpointsResponse>() is { Endpoints.Count: > 0 } fresh)
                        {
                            _store.UpdateEndpoints(link.Hub.PcId, fresh.Endpoints);
                            link.Hub = link.Hub with { Endpoints = fresh.Endpoints.ToList() };
                            endpointsAt = _time.GetUtcNow();
                        }
                    }
                }
                else
                {
                    if (link.Online) _logger.LogWarning("Linked PC {Name} unreachable: {Error}", link.Hub.PcName, reply.Error ?? $"HTTP {reply.Status}");
                    link.Online = false;
                    if (reply.Status == 401)
                    {
                        link.Error = "rejected";
                        wait = RejectedRetry;
                    }
                    else link.Error = reply.Status == 0 ? "unreachable" : $"HTTP {reply.Status}";
                }
                await Task.Delay(wait, _time, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunInboxAsync(Link link, CancellationToken ct)
    {
        long? cursor = null;
        var startedAt = _time.GetUtcNow();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var reply = await link.Client.GetAsync(link.Hub.Endpoints, $"/api/v1/inbox?after={cursor ?? 0}",
                    link.Hub.Token, InboxTimeout, ct);
                if (!reply.Ok || reply.Read<InboxResponse>() is not { } inbox)
                {
                    await Task.Delay(reply.Status == 401 ? RejectedRetry : PresenceInterval, _time, ct);
                    continue;
                }
                foreach (var item in inbox.Items)
                {
                    // On (re)start, don't replay the hub's last 30 minutes as fresh notifications.
                    if (cursor is null && item.Ts < startedAt - TimeSpan.FromSeconds(30)) continue;
                    await RelayFromHubAsync(link, item);
                }
                cursor = inbox.Next;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RelayFromHubAsync(Link link, InboxItem item)
    {
        if (_forwarding is null) return;
        var name = item.OriginId == NotificationInbox.SelfOrigin ? link.LastDevices?.Hub.PcName ?? link.Hub.PcName : item.OriginName;
        var origin = new ToastOrigin($"hub:{link.Hub.PcId}:{item.OriginId}", name, FromHub: true, link.HubPushHashes);
        await _forwarding.AcceptFromDeviceAsync(origin, item.App, item.Title, item.Body, item.Ts, item.Id);
    }

    private void OnLocalNotification(ToastItem item)
    {
        var n = new DeviceNotification(MobileProtocol.Schema, item.Id ?? Guid.NewGuid().ToString("N"),
            Clip(item.SourceApp, DeviceNotification.MaxApp), Clip(item.Title, DeviceNotification.MaxTitle),
            item.Body is { Length: > 0 } b ? Clip(b, DeviceNotification.MaxBody) : null, item.At);
        foreach (var link in _links.Values) link.Outbox.Writer.TryWrite(n);
    }

    private async Task RunOutboxAsync(Link link, CancellationToken ct)
    {
        try
        {
            await foreach (var n in link.Outbox.Reader.ReadAllAsync(ct))
            {
                var reply = await link.Client.PostAsync(link.Hub.Endpoints, "/api/v1/notifications", n, link.Hub.Token, CallTimeout, ct);
                if (!reply.Ok)
                    _logger.LogInformation("Notification to linked PC {Name} not delivered: {Error}", link.Hub.PcName,
                        reply.Error ?? reply.ErrorCode ?? $"HTTP {reply.Status}");
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public override void Dispose()
    {
        foreach (var link in _links.Values)
        {
            link.Cts.Cancel();
            link.Client.Dispose();
        }
        base.Dispose();
    }
}

/// <summary>Seam over <see cref="LocalOpsBot.Infrastructure.Mobile.SystemStatusProbe"/> so tests can supply a fixed report.</summary>
public class SystemStatusProbeAdapter
{
    private readonly Func<IReadOnlyList<string>?, PresenceReport> _report;

    public SystemStatusProbeAdapter(Func<IReadOnlyList<string>?, PresenceReport> report) => _report = report;

    public PresenceReport Report(IReadOnlyList<string>? pushHashes) => _report(pushHashes);
}
