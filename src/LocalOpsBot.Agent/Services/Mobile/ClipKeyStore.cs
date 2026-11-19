using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// The clipboard keys this PC agreed (DECISIONS M-62), by <see cref="ClipTargetKeys"/> key: <c>device:&lt;id&gt;</c>
/// for a device paired here (it asked), <c>pc:&lt;pcId&gt;</c> for a PC this one is linked to (this one asked).
/// A key left behind by a device that is gone is harmless: its id is never reused, and a device that pairs again
/// agrees a new one when the PC answers <c>clip_key_needed</c>.
/// </summary>
public interface IClipKeyStore
{
    byte[]? Get(string key);
    void Set(string key, byte[] value);
}

/// <summary>
/// <c>clip-keys.json</c> next to the device store (same SYSTEM/Administrators ACL). Keys are DPAPI-encrypted
/// (LocalMachine scope), like the link tokens in <c>hubs.json</c>, so a copied file is useless on another machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClipKeyFile : IClipKeyStore
{
    private const int MaxEntries = 256;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Homebase.ClipKey.v1");

    private sealed record Entry(string Value, DateTimeOffset At);

    private readonly string _path;
    private readonly bool _protect;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries;

    public ClipKeyFile(string directory, TimeProvider time, ILogger logger, bool protect = true)
    {
        _path = Path.Combine(directory, "clip-keys.json");
        _protect = protect;
        _time = time;
        _entries = Load(logger);
    }

    public byte[]? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e)) return null;
            try { return Unprotect(e.Value); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        }
    }

    public void Set(string key, byte[] value)
    {
        lock (_gate)
        {
            _entries[key] = new Entry(Protect(value), _time.GetUtcNow());
            if (_entries.Count > MaxEntries)
                foreach (var old in _entries.OrderBy(p => p.Value.At).Take(_entries.Count - MaxEntries).Select(p => p.Key).ToList())
                    _entries.Remove(old);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, MobileProtocol.Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>A corrupt file is set aside and the store starts empty: devices agree new keys on their next send.</summary>
    private Dictionary<string, Entry> Load(ILogger logger)
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), MobileProtocol.Json);
            if (loaded is not null) return new(loaded, StringComparer.Ordinal);
        }
        catch (JsonException) { }
        var aside = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try { File.Move(_path, aside); } catch (IOException) { }
        logger.LogError("Clipboard key store {Path} was unreadable; kept it as {Aside} and started empty.", _path, aside);
        return new(StringComparer.Ordinal);
    }

    private string Protect(byte[] value) => _protect
        ? Convert.ToBase64String(ProtectedData.Protect(value, Entropy, DataProtectionScope.LocalMachine))
        : "plain:" + Convert.ToBase64String(value);

    private byte[] Unprotect(string stored) => stored.StartsWith("plain:", StringComparison.Ordinal) && !_protect
        ? Convert.FromBase64String(stored["plain:".Length..])
        : ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.LocalMachine);
}
