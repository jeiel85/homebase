using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

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
        public string TokenSha256 { get; set; } = "";
        public DateTimeOffset PairedAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }

    public MobileDeviceStore(string directory, TimeProvider time, bool restrictAcl)
    {
        _time = time;
        Directory.CreateDirectory(directory);
        if (restrictAcl) RestrictToSystemAndAdmins(directory);
        _path = Path.Combine(directory, "state.json");
        _state = Load(_path);
        if (string.IsNullOrEmpty(_state.PcId))
        {
            _state.PcId = Guid.NewGuid().ToString("N");
            Save();
        }
    }

    public string PcId => _state.PcId;

    public IReadOnlyList<MobileDeviceInfo> List()
    {
        lock (_gate) return _state.Devices.Select(ToInfo).ToList();
    }

    public MobileDeviceInfo Add(string name, string? model, string tokenSha256Hex)
    {
        lock (_gate)
        {
            var device = new Device
            {
                DeviceId = Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Model = model,
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

    private static MobileDeviceInfo ToInfo(Device d) => new(d.DeviceId, d.Name, d.Model, d.PairedAt, d.LastSeenAt);

    private static State Load(string path)
    {
        if (!File.Exists(path)) return new State();
        // A corrupt file must not silently become "no devices + new PC id" — that would orphan
        // every paired phone. Fail loudly; the operator can inspect or delete it.
        return JsonSerializer.Deserialize<State>(File.ReadAllText(path), MobileProtocol.Json)
               ?? throw new InvalidDataException($"Mobile device store '{path}' is empty or invalid.");
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
