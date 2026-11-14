using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// Paired phones + this PC's id in one small JSON file
/// (<c>C:\ProgramData\Homebase\mobile\state.json</c>). The folder is ACL'd to SYSTEM and
/// Administrators only. Tokens are never stored — only their SHA-256.
/// Kept out of SQLite until Device Hub GOAL-05 settles the V2 <c>device</c> table (DECISIONS M-11).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MobileDeviceStore : IMobileDeviceStore
{
    public static readonly string DefaultDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Homebase", "mobile");

    // Last-seen is informational; don't rewrite the file on every request.
    private static readonly TimeSpan LastSeenPersistInterval = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ILogger _log;
    private State _state;

    private sealed class State
    {
        public string PcId { get; set; } = "";
        public List<Device> Devices { get; set; } = new();
    }

    private sealed class Device
    {
        public string DeviceId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Model { get; set; }
        /// <summary><c>phone</c> or <c>pc</c>; absent in files written before v0.22.0 (all phones then).</summary>
        public string? Kind { get; set; }
        public string TokenSha256 { get; set; } = "";
        public DateTimeOffset PairedAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
        /// <summary>UnifiedPush endpoint and RFC 8291 keys (public key + auth secret); not secret-free, so ACL'd like the rest.</summary>
        public PushSubscription? Push { get; set; }
    }

    public MobileDeviceStore(string directory, TimeProvider time, bool restrictAcl, ILogger? logger = null)
    {
        _time = time;
        var log = _log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(directory);
        if (restrictAcl)
        {
            try { RestrictToSystemAndAdmins(directory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
            {
                log.LogWarning(ex, "Could not restrict the ACL of {Directory}.", directory);
            }
        }
        _path = Path.Combine(directory, "state.json");
        _state = Load(_path, log);
        var dirty = PruneDuplicatesLocked();
        if (string.IsNullOrEmpty(_state.PcId))
        {
            _state.PcId = Guid.NewGuid().ToString("N");
            dirty = true;
        }
        if (dirty) Save();
    }

    public string PcId => _state.PcId;

    public IReadOnlyList<MobileDeviceInfo> List()
    {
        lock (_gate) return _state.Devices.Select(ToInfo).ToList();
    }

    public MobileDeviceInfo Add(string name, string? model, string tokenSha256Hex, string kind = DeviceKind.Phone)
    {
        lock (_gate)
        {
            var device = new Device
            {
                DeviceId = Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Model = model,
                Kind = DeviceKind.Normalize(kind),
                TokenSha256 = tokenSha256Hex,
                PairedAt = _time.GetUtcNow(),
            };
            _state.Devices.Add(device);
            Save();
            return ToInfo(device);
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_gate)
        {
            var removed = _state.Devices.RemoveAll(d => d.DeviceId == deviceId) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public bool IsPaired(string deviceId)
    {
        lock (_gate) return _state.Devices.Exists(d => d.DeviceId == deviceId);
    }

    public bool SetPushSubscription(string deviceId, PushSubscription? subscription)
    {
        lock (_gate)
        {
            var device = _state.Devices.Find(d => d.DeviceId == deviceId);
            if (device is null) return false;
            if (device.Push == subscription) return true;
            device.Push = subscription;
            if (subscription is not null) RevokeSameEndpointLocked(device);
            Save();
            return true;
        }
    }

    public IReadOnlyList<PushTarget> PushTargets()
    {
        lock (_gate)
        {
            return _state.Devices
                .Where(d => d.Push is not null)
                .Select(d => new PushTarget(d.DeviceId, d.Name, d.Push!, d.TokenSha256))
                .ToList();
        }
    }

    public MobileDeviceInfo? Authenticate(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var presented = Encoding.ASCII.GetBytes(PairingService.HashToken(token));
        lock (_gate)
        {
            foreach (var d in _state.Devices)
            {
                if (!CryptographicOperations.FixedTimeEquals(presented, Encoding.ASCII.GetBytes(d.TokenSha256))) continue;
                var now = _time.GetUtcNow();
                if (d.LastSeenAt is null || now - d.LastSeenAt > LastSeenPersistInterval)
                {
                    d.LastSeenAt = now;
                    Save();
                }
                return ToInfo(d);
            }
            return null;
        }
    }

    /// <summary>
    /// A push endpoint belongs to one app install, and the app keeps one token per PC — so another
    /// device with the same endpoint is that phone's earlier pairing, left behind by a re-pair. It is
    /// revoked; otherwise every alert goes to the phone twice (ntfy quota) and the Tray lists it twice.
    /// </summary>
    private void RevokeSameEndpointLocked(Device keep)
    {
        foreach (var old in _state.Devices.Where(d => d != keep && d.Push?.Endpoint == keep.Push?.Endpoint).ToList())
        {
            _state.Devices.Remove(old);
            _log.LogInformation("Removed {Name} ({Old}): the same phone paired again as {New}.", old.Name, old.DeviceId, keep.DeviceId);
        }
    }

    /// <summary>Clean-up for stores written before the rule above: keeps the newest pairing per endpoint.</summary>
    private bool PruneDuplicatesLocked()
    {
        var dirty = false;
        foreach (var group in _state.Devices.Where(d => d.Push is not null).GroupBy(d => d.Push!.Endpoint, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1).ToList())
        {
            RevokeSameEndpointLocked(group.OrderByDescending(d => d.PairedAt).First());
            dirty = true;
        }
        return dirty;
    }

    private static MobileDeviceInfo ToInfo(Device d) => new(d.DeviceId, d.Name, d.Model, d.PairedAt, d.LastSeenAt, DeviceKind.Normalize(d.Kind));

    /// <summary>
    /// A corrupt file must neither take the Agent down (Telegram monitoring keeps running) nor be
    /// silently overwritten: it is moved aside as <c>state.json.corrupt-*</c> and logged, and the
    /// store starts empty — paired phones then get 401 and are told to re-pair.
    /// </summary>
    private static State Load(string path, ILogger log)
    {
        if (!File.Exists(path)) return new State();
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), MobileProtocol.Json);
            if (state is not null) return state;
        }
        catch (JsonException) { }

        var aside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, aside);
        log.LogError("Mobile device store {Path} was unreadable; kept it as {Aside} and started empty. Paired phones must pair again.", path, aside);
        return new State();
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, MobileProtocol.Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static void RestrictToSystemAndAdmins(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
