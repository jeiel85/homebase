using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>A hub PC this PC paired with as a device (DECISIONS M-29).</summary>
public sealed record LinkedHub(
    string PcId,
    string PcName,
    string DeviceId,
    string Token,
    IReadOnlyList<MobileEndpoint> Endpoints,
    DateTimeOffset LinkedAt);

/// <summary>
/// <c>hubs.json</c> next to the device store (same SYSTEM/Administrators ACL). Tokens are stored
/// DPAPI-encrypted (LocalMachine scope), so a copied file is useless on another machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HubStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Homebase.HubLink.v1");

    private sealed record Row(string PcId, string PcName, string DeviceId, string TokenProtected,
        List<MobileEndpoint> Endpoints, DateTimeOffset LinkedAt);

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly bool _protect;
    private readonly object _gate = new();
    private List<LinkedHub> _hubs;

    public HubStore(string directory, ILogger logger, bool protect = true)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "hubs.json");
        _logger = logger;
        _protect = protect;
        _hubs = Load();
    }

    public IReadOnlyList<LinkedHub> List()
    {
        lock (_gate) return _hubs.ToList();
    }

    public void Upsert(LinkedHub hub)
    {
        lock (_gate)
        {
            _hubs.RemoveAll(h => h.PcId == hub.PcId);
            _hubs.Add(hub);
            Save();
        }
    }

    public void UpdateEndpoints(string pcId, IReadOnlyList<MobileEndpoint> endpoints)
    {
        lock (_gate)
        {
            var i = _hubs.FindIndex(h => h.PcId == pcId);
            if (i < 0 || _hubs[i].Endpoints.SequenceEqual(endpoints)) return;
            _hubs[i] = _hubs[i] with { Endpoints = endpoints.ToList() };
            Save();
        }
    }

    public bool Remove(string pcId)
    {
        lock (_gate)
        {
            var removed = _hubs.RemoveAll(h => h.PcId == pcId) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private List<LinkedHub> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(_path), MobileProtocol.Json) ?? new();
            var list = new List<LinkedHub>();
            foreach (var r in rows)
            {
                try
                {
                    list.Add(new LinkedHub(r.PcId, r.PcName, r.DeviceId, Unprotect(r.TokenProtected), r.Endpoints, r.LinkedAt));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    _logger.LogWarning("Linked PC {Name}: its token can't be decrypted on this machine; link it again.", r.PcName);
                }
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "hubs.json is unreadable; starting with no linked PCs.");
            return new();
        }
    }

    private void Save()
    {
        var rows = _hubs.Select(h => new Row(h.PcId, h.PcName, h.DeviceId, Protect(h.Token), h.Endpoints.ToList(), h.LinkedAt)).ToList();
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rows, MobileProtocol.Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private string Protect(string token) => _protect
        ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine))
        : "plain:" + token;

    private string Unprotect(string stored) => stored.StartsWith("plain:", StringComparison.Ordinal) && !_protect
        ? stored["plain:".Length..]
        : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.LocalMachine));
}
