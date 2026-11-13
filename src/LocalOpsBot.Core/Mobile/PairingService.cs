using System.Security.Cryptography;

namespace LocalOpsBot.Core.Mobile;

/// <summary>Paired-phone registry. Only token hashes are persisted.</summary>
public interface IMobileDeviceStore
{
    /// <summary>Stable id of this PC, created on first use.</summary>
    string PcId { get; }

    IReadOnlyList<MobileDeviceInfo> List();

    MobileDeviceInfo Add(string name, string? model, string tokenSha256Hex);

    bool Revoke(string deviceId);

    /// <summary>The device the bearer token belongs to, or <c>null</c>. Records last-seen time.</summary>
    MobileDeviceInfo? Authenticate(string token);
}

public enum PairOutcome { Paired, InvalidCode, Expired, Locked }

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

    public PairResult TryPair(string? code, string? deviceName, string? deviceModel)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            while (_failures.Count > 0 && now - _failures.Peek() > FailureWindow) _failures.Dequeue();
            if (_failures.Count >= MaxFailures) return new PairResult(PairOutcome.Locked);

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
            var device = _store.Add(CleanName(deviceName), CleanModel(deviceModel), HashToken(token));
            return new PairResult(PairOutcome.Paired, device.DeviceId, token);
        }
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "Android";
        return n.Length > 64 ? n[..64] : n;
    }

    private static string? CleanModel(string? model)
    {
        var m = model?.Trim();
        if (string.IsNullOrEmpty(m)) return null;
        return m.Length > 64 ? m[..64] : m;
    }
}
