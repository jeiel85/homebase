using System.Security.Cryptography;

namespace LocalOpsBot.Core.Mobile;

/// <summary>Paired-phone registry. Only token hashes are persisted.</summary>
public interface IMobileDeviceStore
{
    /// <summary>Stable id of this PC, created on first use.</summary>
    string PcId { get; }

    IReadOnlyList<MobileDeviceInfo> List();

    MobileDeviceInfo Add(string name, string? model, string tokenSha256Hex, string kind = DeviceKind.Phone, string? pcId = null);

    /// <summary>
    /// Binds a paired PC device to its immutable <paramref name="pcId"/> (on first presence for pairings from
    /// before <c>pcId</c> was stored). Returns <c>true</c> when the device is bound to <paramref name="pcId"/>,
    /// or <c>false</c> when it is already bound to a different PC id or another paired PC owns <paramref name="pcId"/>.
    /// </summary>
    bool BindPcId(string deviceId, string pcId);

    bool Revoke(string deviceId);

    /// <summary>The device the bearer token belongs to, or <c>null</c>. Records last-seen time.</summary>
    MobileDeviceInfo? Authenticate(string token);

    /// <summary>Whether the device is still paired (not revoked). Cheap; checked before every live push.</summary>
    bool IsPaired(string deviceId);

    /// <summary>
    /// Stores (or with <c>null</c> clears) the device's UnifiedPush subscription. Returns <c>false</c>
    /// when the device is not paired. Revoking a device drops its subscription with it. Any other device
    /// with the same endpoint is the same phone's earlier pairing and is revoked.
    /// </summary>
    bool SetPushSubscription(string deviceId, LocalOpsBot.Core.Push.PushSubscription? subscription);

    /// <summary>Every paired device that has a push subscription.</summary>
    IReadOnlyList<LocalOpsBot.Core.Push.PushTarget> PushTargets();
}

public enum PairOutcome { Paired, InvalidCode, Expired, Locked, Forbidden }

public sealed record PairResult(PairOutcome Outcome, string? DeviceId = null, string? Token = null);

/// <summary>
/// One-time QR pairing codes. One active code at a time (a new <see cref="Begin"/> replaces the
/// last), 5-minute lifetime, single use, and a lockout after repeated wrong codes so the 128-bit
/// code can't be ground down even by a tailnet peer.
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    public const int MaxFailures = 10;

    private readonly IMobileDeviceStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private string? _code;
    private DateTimeOffset _expiresAt;

    /// <summary>
    /// Group introduction codes (M-47): handed by one member to another so it can pair without a QR. Kept
    /// apart from the QR code so an introduction never cancels a code on screen; each is single use and
    /// bound to an allowed <see cref="DeviceKind"/> so a linked PC cannot mint a phone token.
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, string AllowedKind)> _introductions = new(StringComparer.Ordinal);
    public const int MaxIntroductions = 8;

    public PairingService(IMobileDeviceStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    public (string Code, DateTimeOffset ExpiresAt) Begin()
    {
        lock (_gate)
        {
            _code = Base64Url(RandomNumberGenerator.GetBytes(16));
            _expiresAt = _time.GetUtcNow() + CodeLifetime;
            return (_code, _expiresAt);
        }
    }

    /// <summary>A one-time code for a group introduction (M-47); the QR code stays valid. Defaults to PC-only pairing.</summary>
    public (string Code, DateTimeOffset ExpiresAt) BeginIntroduction(string allowedKind = DeviceKind.Pc)
    {
        var kind = DeviceKind.Normalize(allowedKind);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var old in _introductions.Where(kv => kv.Value.ExpiresAt < now).Select(kv => kv.Key).ToList()) _introductions.Remove(old);
            while (_introductions.Count >= MaxIntroductions) _introductions.Remove(_introductions.MinBy(kv => kv.Value.ExpiresAt).Key);
            var code = Base64Url(RandomNumberGenerator.GetBytes(16));
            var expires = now + CodeLifetime;
            _introductions[code] = (expires, kind);
            return (code, expires);
        }
    }

    public PairResult TryPair(string? code, string? deviceName, string? deviceModel, string? kind = null, string? pcId = null)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            while (_failures.Count > 0 && now - _failures.Peek() > FailureWindow) _failures.Dequeue();
            if (_failures.Count >= MaxFailures) return new PairResult(PairOutcome.Locked);

            var normalizedKind = DeviceKind.Normalize(kind);
            var cleanPcId = normalizedKind == DeviceKind.Pc ? CleanPcId(pcId) : null;

            if (!string.IsNullOrEmpty(code) && _introductions.Remove(code, out var intro))
            {
                if (now > intro.ExpiresAt) return new PairResult(PairOutcome.Expired);
                if (!string.Equals(normalizedKind, intro.AllowedKind, StringComparison.Ordinal))
                    return new PairResult(PairOutcome.Forbidden);
                var introToken = Base64Url(RandomNumberGenerator.GetBytes(32));
                var introduced = _store.Add(CleanName(deviceName, kind), CleanModel(deviceModel), HashToken(introToken), normalizedKind, cleanPcId);
                return new PairResult(PairOutcome.Paired, introduced.DeviceId, introToken);
            }

            if (_code is null || string.IsNullOrEmpty(code) ||
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(_code), System.Text.Encoding.ASCII.GetBytes(code)))
            {
                _failures.Enqueue(now);
                return new PairResult(PairOutcome.InvalidCode);
            }

            if (now > _expiresAt)
            {
                _code = null;
                return new PairResult(PairOutcome.Expired);
            }

            _code = null; // single use
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            var device = _store.Add(CleanName(deviceName, kind), CleanModel(deviceModel), HashToken(token), normalizedKind, cleanPcId);
            return new PairResult(PairOutcome.Paired, device.DeviceId, token);
        }
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CleanName(string? name, string? kind = null)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return DeviceKind.Normalize(kind) == DeviceKind.Pc ? "PC" : "Android";
        return n.Length > 64 ? n[..64] : n;
    }

    private static string? CleanModel(string? model)
    {
        var m = model?.Trim();
        if (string.IsNullOrEmpty(m)) return null;
        return m.Length > 64 ? m[..64] : m;
    }

    private static string? CleanPcId(string? pcId)
    {
        var id = pcId?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        return id.Length > 64 ? id[..64] : id;
    }
}
