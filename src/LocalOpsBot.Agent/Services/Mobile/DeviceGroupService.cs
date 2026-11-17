using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Keeps this PC's device group whole (DECISIONS M-47). Connecting to any member means joining the
/// group, so:
/// <list type="bullet">
/// <item>a link is made both ways: after this PC links to another, it hands that PC a fresh offer of its
/// own (<c>POST /api/v1/group/join</c>) so the other side links back;</item>
/// <item>every 2 minutes it asks each linked PC for its group (<c>GET /api/v1/group</c>) and links to the
/// PCs it is missing through an introduction (<c>POST /api/v1/group/introduce</c>: the member that holds
/// that PC's token fetches a one-time offer for it);</item>
/// <item>a device taken out of the group on any member is taken out here too (removals travel with the
/// group answers and are kept for a week, so a member that was offline catches up);</item>
/// <item>the group's aliases (M-51) travel the same way: each answer carries this PC's table, each sync
/// merges the other's, and an alias set on this PC's tray goes to every linked PC at once.</item>
/// </list>
/// Phones join the same way from their side: they read the group from any PC and pair with the rest.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceGroupService : BackgroundService
{
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstSync = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly HubLinkService _links;
    private readonly IMobileDeviceStore _devices;
    private readonly DeviceStatusBoard _board;
    private readonly GroupTombstones _removed;
    private readonly DeviceAliasTable _aliases;
    private readonly Func<PairingOffer?> _ownOffer;
    private readonly string _pcName;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _syncing = new(1, 1);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DeviceGroupService(
        HubLinkService links,
        IMobileDeviceStore devices,
        DeviceStatusBoard board,
        GroupTombstones removed,
        Func<PairingOffer?> ownOffer,
        string pcName,
        TimeProvider time,
        ILogger<DeviceGroupService> logger,
        DeviceAliasTable? aliases = null)
    {
        _aliases = aliases ?? new DeviceAliasTable(time);
        _links = links;
        _devices = devices;
        _board = board;
        _removed = removed;
        _ownOffer = ownOffer;
        _pcName = pcName;
        _time = time;
        _logger = logger;
        _links.Linked += OnLinked;
    }

    /// <summary>This PC and the PCs it is linked with (either way), with recent removals.</summary>
    public GroupResponse Describe()
    {
        var pcs = new Dictionary<string, GroupMember>(StringComparer.Ordinal);
        foreach (var h in _links.Hubs()) pcs[h.PcId] = new GroupMember(h.PcId, h.PcName, Token: true);
        foreach (var d in _devices.List().Where(d => d.Kind == DeviceKind.Pc))
        {
            if (_board.PcIdOf(d.DeviceId) is { } pcId && pcId != _devices.PcId && !pcs.ContainsKey(pcId))
                pcs[pcId] = new GroupMember(pcId, d.Name, Token: false);
        }
        return new GroupResponse(MobileProtocol.Schema, _devices.PcId, _pcName, pcs.Values.OrderBy(p => p.PcName).ToList(),
            _removed.List(), _aliases.List());
    }

    /// <summary>The group's aliases (M-51), cleared ones included.</summary>
    public DeviceAliasTable Aliases => _aliases;

    /// <summary>
    /// The user named a device on this PC's tray (M-51): kept here at once and sent in the background to
    /// every PC this one holds a token for; a PC that is off gets it from the next sync. False when nothing
    /// changed (the same alias, or not a group key).
    /// </summary>
    public bool SetAlias(string key, string name, string? alias)
    {
        if (_aliases.Set(key, name, alias) is not { } entry) return false;
        _logger.LogInformation("Group: alias for {Key} {Action} on this PC.", key, entry.Alias is null ? "cleared" : "set");
        _ = Task.Run(() => ShareAliasAsync(entry, _stopping));
        return true;
    }

    /// <summary>Hands one entry to every PC this one holds a token for (each call is bounded by its own timeout).</summary>
    public async Task ShareAliasAsync(DeviceAlias entry, CancellationToken ct)
    {
        var body = new GroupAliasRequest(MobileProtocol.Schema, new[] { entry });
        try
        {
            await Task.WhenAll(_links.Hubs().Select(h => _links.CallAsync(h.PcId, HttpMethod.Post, GroupPaths.Alias, body, CallTimeout, ct)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Group: sharing the alias for {Key} failed; the next sync carries it.", entry.Key);
        }
    }

    /// <summary><c>POST /api/v1/group/alias</c>: what a phone or PC of the group set; merged, not passed on (one hop).</summary>
    public int AcceptAliases(IReadOnlyList<DeviceAlias> aliases)
    {
        var n = _aliases.Merge(aliases.Take(DeviceAliasTable.MaxPerRequest));
        if (n > 0) _logger.LogInformation("Group: {Count} alias(es) from a member.", n);
        return n;
    }

    /// <summary><c>POST /api/v1/group/join</c>: link to the PC behind <paramref name="offer"/> unless already linked.</summary>
    public async Task<GroupJoinResponse> JoinAsync(PairingOffer offer, CancellationToken ct)
    {
        if (offer.PcId == _devices.PcId) return new(MobileProtocol.Schema, false, false, offer.PcName, "self");
        if (_removed.IsRemoved(GroupKeys.Pc(offer.PcId))) return new(MobileProtocol.Schema, false, false, offer.PcName, "removed");
        if (_links.Hub(offer.PcId) is not null) return new(MobileProtocol.Schema, false, true, offer.PcName, null);
        var r = await _links.LinkWithOfferAsync(offer, ct);
        if (r.Linked) _logger.LogInformation("Group: linked to {Name} on a member's introduction.", r.PcName);
        return new(MobileProtocol.Schema, r.Linked, false, r.PcName ?? offer.PcName, r.Error);
    }

    /// <summary><c>POST /api/v1/group/introduce</c>: a fresh offer of <paramref name="pcId"/>, which this PC holds a token for.</summary>
    public async Task<PairingOffer?> IntroduceAsync(string pcId, CancellationToken ct)
    {
        if (_removed.IsRemoved(GroupKeys.Pc(pcId)) || _links.Hub(pcId) is null) return null;
        var reply = await _links.CallAsync(pcId, HttpMethod.Post, GroupPaths.Offer, new { schema = MobileProtocol.Schema }, CallTimeout, ct);
        return reply is { Ok: true } ? reply.Read<PairingOffer>() : null;
    }

    /// <summary>
    /// Takes a device out of the group: here, and — when <paramref name="fanOut"/> (the user did it on this
    /// PC) — on every PC this one holds a token for. Returns how many devices were removed here.
    /// </summary>
    public async Task<int> RemoveAsync(string key, bool fanOut, CancellationToken ct)
    {
        if (!GroupKeys.IsValid(key)) return 0;
        _removed.Add(key);
        if (fanOut)
        {
            // Tell the others first, while this PC's tokens for them still work — the removed PC too,
            // so it drops its own links instead of retrying them.
            await Task.WhenAll(_links.Hubs().Select(h =>
                _links.CallAsync(h.PcId, HttpMethod.Post, GroupPaths.Remove, new GroupRemoveRequest(MobileProtocol.Schema, key), CallTimeout, ct)));
        }
        var n = await ApplyAsync(key);
        _logger.LogInformation("Group: removed {Key} ({Count} here{FanOut}).", key, n, fanOut ? ", told the group" : "");
        return n;
    }

    /// <summary>Pairing by hand brings a device back: forget it was removed (M-47).</summary>
    public void Readmit(string key) => _removed.Clear(key);

    /// <summary>The group key of a device paired here, for removing it everywhere.</summary>
    public string? KeyOf(MobileDeviceInfo d)
    {
        if (d.Kind == DeviceKind.Pc) return _board.PcIdOf(d.DeviceId) is { } pcId ? GroupKeys.Pc(pcId) : null;
        var push = _devices.PushTargets().FirstOrDefault(t => t.DeviceId == d.DeviceId);
        return push is not null ? GroupKeys.PhoneByPush(PushHash.Of(push.Subscription.Endpoint)) : GroupKeys.PhoneByName(d.Name, d.Model);
    }

    private async Task<int> ApplyAsync(string key)
    {
        var n = 0;
        if (GroupKeys.PcIdOf(key) is { } pcId)
        {
            if (pcId == _devices.PcId)
            {
                // The group took this PC out: drop every PC link (the others already revoked theirs).
                foreach (var h in _links.Hubs()) if (await _links.UnlinkAsync(h.PcId)) n++;
                foreach (var d in _devices.List().Where(d => d.Kind == DeviceKind.Pc)) if (_devices.Revoke(d.DeviceId)) n++;
                return n;
            }
            if (_links.Hub(pcId) is not null && await _links.UnlinkAsync(pcId)) n++;
            foreach (var d in _devices.List().Where(d => d.Kind == DeviceKind.Pc && _board.PcIdOf(d.DeviceId) == pcId))
                if (_devices.Revoke(d.DeviceId)) n++;
            return n;
        }
        foreach (var d in _devices.List().Where(d => d.Kind == DeviceKind.Phone && KeyOf(d) is { } k && (k == key || GroupKeys.PhoneByName(d.Name, d.Model) == key)))
            if (_devices.Revoke(d.DeviceId)) n++;
        return n;
    }

    /// <summary>Runs a sync soon (after a link or a removal).</summary>
    public void Nudge() => Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private void OnLinked(LinkedHub hub) => _ = Task.Run(async () =>
    {
        // The other side links back with this PC's offer, so both hold a token (live view, relays, group).
        try { await AskToLinkBackAsync(hub.PcId, _stopping); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _logger.LogDebug(ex, "Group: link back from {Name} failed", hub.PcName); }
        Nudge();
    });

    private CancellationToken _stopping;

    private async Task AskToLinkBackAsync(string pcId, CancellationToken ct)
    {
        if (_ownOffer() is not { } offer) return;
        var reply = await _links.CallAsync(pcId, HttpMethod.Post, GroupPaths.Join, new GroupJoinRequest(MobileProtocol.Schema, offer), CallTimeout, ct);
        if (reply is { Ok: true } && reply.Read<GroupJoinResponse>() is { } r && r is { Linked: false, Already: false })
            _logger.LogInformation("Group: {Pc} could not link back ({Error}).", pcId, r.Error);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            await Task.Delay(FirstSync, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await SyncAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Group sync failed"); }
                var wake = Volatile.Read(ref _wake).Task;
                await Task.WhenAny(Task.Delay(SyncInterval, _time, stoppingToken), wake);
                stoppingToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One pass over the linked PCs: catch up on removals, make links mutual, link to missing members.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (!await _syncing.WaitAsync(0, ct)) return;
        try
        {
            var me = _devices.PcId;
            foreach (var hub in _links.Hubs())
            {
                if (_links.Hub(hub.PcId) is null) continue; // removed by an earlier step of this pass
                var reply = await _links.CallAsync(hub.PcId, HttpMethod.Get, GroupPaths.Group, null, CallTimeout, ct);
                if (reply is not { Ok: true } || reply.Read<GroupResponse>() is not { } group) continue;

                foreach (var key in _removed.Merge(group.Removed ?? Array.Empty<GroupRemoval>()))
                {
                    var n = await ApplyAsync(key);
                    _logger.LogInformation("Group: {Name} reported {Key} removed ({Count} here).", hub.PcName, key, n);
                }
                if (group.Aliases is { Count: > 0 } aliases && _aliases.Merge(aliases) is > 0 and var renamed)
                    _logger.LogInformation("Group: {Count} alias(es) from {Name}.", renamed, hub.PcName);
                if (_links.Hub(hub.PcId) is null) continue;

                // The hub should hold this PC's token too (a link made before v0.35.0, or a link back that failed).
                if (!group.Pcs.Any(p => p.PcId == me && p.Token) && Due("back:" + hub.PcId))
                {
                    Failed("back:" + hub.PcId); // asked: not again for a while, whatever the answer
                    await AskToLinkBackAsync(hub.PcId, ct);
                }

                foreach (var p in group.Pcs)
                {
                    if (p.PcId == me || !p.Token || _links.Hub(p.PcId) is not null) continue;
                    if (_removed.IsRemoved(GroupKeys.Pc(p.PcId)) || !Due(p.PcId)) continue;
                    var offer = await IntroduceThroughAsync(hub.PcId, p.PcId, ct);
                    if (offer is null) { Failed(p.PcId); continue; }
                    var r = await _links.LinkWithOfferAsync(offer, ct);
                    if (r.Linked) _logger.LogInformation("Group: linked to {Name}, introduced by {Hub}.", r.PcName, hub.PcName);
                    else
                    {
                        Failed(p.PcId);
                        _logger.LogInformation("Group: could not link to {Name} introduced by {Hub}: {Error}", p.PcName, hub.PcName, r.Message ?? r.Error);
                    }
                }
            }
        }
        finally
        {
            _syncing.Release();
        }
    }

    private async Task<PairingOffer?> IntroduceThroughAsync(string viaPcId, string pcId, CancellationToken ct)
    {
        var reply = await _links.CallAsync(viaPcId, HttpMethod.Post, GroupPaths.Introduce, new GroupIntroduceRequest(MobileProtocol.Schema, pcId), CallTimeout, ct);
        return reply is { Ok: true } ? reply.Read<PairingOffer>() : null;
    }

    private bool Due(string key) => !_failedAt.TryGetValue(key, out var at) || _time.GetUtcNow() - at >= FailureBackoff;

    private void Failed(string key) => _failedAt[key] = _time.GetUtcNow();

    public override void Dispose()
    {
        _links.Linked -= OnLinked;
        base.Dispose();
    }
}

/// <summary>The group's aliases persisted in <c>aliases.json</c> next to <c>group.json</c> (same ACL; M-51).</summary>
public static class DeviceAliasFile
{
    public static DeviceAliasTable Open(string directory, TimeProvider time, ILogger logger)
    {
        var path = Path.Combine(directory, "aliases.json");
        IReadOnlyList<DeviceAlias>? initial = null;
        try
        {
            if (File.Exists(path)) initial = JsonSerializer.Deserialize<List<DeviceAlias>>(File.ReadAllText(path), MobileProtocol.Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("aliases.json unreadable ({Message}); starting with no aliases.", ex.Message);
        }
        var table = new DeviceAliasTable(time, initial);
        var write = new object();
        table.Changed += () =>
        {
            lock (write)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(table.List(), FileJson));
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("aliases.json not saved: {Message}", ex.Message);
                }
            }
        };
        return table;
    }

    // Korean and emoji stay readable in the file.
    private static readonly JsonSerializerOptions FileJson = new(MobileProtocol.Json)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };
}

/// <summary>Removals persisted in <c>group.json</c> next to <c>state.json</c> (same ACL: SYSTEM and Administrators).</summary>
public static class GroupTombstoneFile
{
    public static GroupTombstones Open(string directory, TimeProvider time, ILogger logger)
    {
        var path = Path.Combine(directory, "group.json");
        IReadOnlyList<GroupRemoval>? initial = null;
        try
        {
            if (File.Exists(path)) initial = JsonSerializer.Deserialize<List<GroupRemoval>>(File.ReadAllText(path), MobileProtocol.Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("group.json unreadable ({Message}); starting with no removals.", ex.Message);
        }
        var t = new GroupTombstones(time, initial);
        t.Changed += () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(t.List(), MobileProtocol.Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("group.json not saved: {Message}", ex.Message);
            }
        };
        return t;
    }
}
