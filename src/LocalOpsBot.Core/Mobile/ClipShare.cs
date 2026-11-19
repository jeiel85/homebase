using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalOpsBot.Core.Push;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Clipboard text sent by hand between paired devices (DECISIONS M-62, <c>docs/CLIPBOARD_SHARE.md</c> phase 1):
/// <list type="bullet">
/// <item>A phone or a linked PC sends a PC text with <c>POST /api/v1/clip</c> (<see cref="ClipEnvelope"/>);
/// the PC's Tray puts it on the clipboard.</item>
/// <item>A PC sends a phone text as a push (<see cref="ClipPushEnvelope"/>, kind <see cref="ClipProtocol.PushKind"/>).
/// Text too long for one push stays on the PC for <see cref="ClipProtocol.OfferLifetime"/>; the phone fetches it
/// with <c>GET /api/v1/clip?id=</c>.</item>
/// </list>
/// The text is always encrypted end to end (<see cref="ClipCrypto"/>) with a key both ends already hold: the
/// SHA-256 of that device's token at that PC. The Cloudflare path ends TLS at Cloudflare's edge, and every
/// paired PC shares a phone's push keys, so neither the tunnel nor another PC reads or forges the text.
/// </summary>
public static class ClipProtocol
{
    public const string Path = "/api/v1/clip";

    /// <summary><c>kind</c> of the push that carries clipboard text to a phone.</summary>
    public const string PushKind = "clip";

    /// <summary>
    /// What a phone that takes clipboard pushes lists in <c>features</c> of <c>POST /api/v1/push</c>. A PC only
    /// offers phones that list it: an older app drops the push without a word.
    /// </summary>
    public const string Feature = "clip";

    /// <summary>Longest text sent at once, in UTF-16 code units (what both platforms call a string's length).</summary>
    public const int MaxChars = 100_000;

    /// <summary>Request body cap for <c>POST /api/v1/clip</c>: <see cref="MaxChars"/> escaped and encoded fits.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>How long a PC keeps text too long for a push, for the phone to fetch.</summary>
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Push TTL: text sent by hand is meant for now. A phone that was off for longer gets nothing rather than a
    /// clipboard that changes under it much later.
    /// </summary>
    public const int PushTtlSeconds = 600;

    /// <summary>
    /// The encrypted inner JSON keeps non-ASCII as UTF-8 (a Korean character costs 3 bytes, not a 6-byte
    /// <c>\uXXXX</c>), so twice as much fits in one push. It is never embedded in HTML.
    /// </summary>
    public static readonly JsonSerializerOptions ContentJson = new(MobileProtocol.Json)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A message id: 32 lowercase hex characters (a UUID without dashes).</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// AES-256-GCM with a per-device key: HKDF-SHA256 (RFC 5869) over the raw 32-byte SHA-256 of the device's token,
/// salt 32 zero bytes, info <c>"homebase clip v1"</c>. A 12-byte random nonce per message; the data is the
/// ciphertext followed by the 16-byte tag, as Java's <c>AES/GCM/NoPadding</c> writes it. No additional data.
/// </summary>
public static class ClipCrypto
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    private static readonly byte[] Info = "homebase clip v1"u8.ToArray();
    private static readonly byte[] Salt = new byte[32];

    /// <summary>The key for the device whose token hashes to <paramref name="tokenSha256Hex"/>.</summary>
    public static byte[] Key(string tokenSha256Hex) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(tokenSha256Hex), KeyBytes, Salt, Info);

    /// <summary>Encrypts; <paramref name="nonce"/> is for tests only (a fixed nonce makes a fixture).</summary>
    public static (string Nonce, string Data) Seal(byte[] key, ReadOnlySpan<byte> plaintext, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
        var output = new byte[plaintext.Length + TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length));
        return (Base64Url.Encode(nonce), Base64Url.Encode(output));
    }

    /// <summary>Decrypts, or <c>null</c> when the values are malformed or the tag does not match (wrong key, altered).</summary>
    public static byte[]? Open(byte[] key, string? nonce, string? data)
    {
        try
        {
            var n = Base64Url.Decode(nonce);
            var d = Base64Url.Decode(data);
            if (n.Length != NonceBytes || d.Length < TagBytes) return null;
            var plain = new byte[d.Length - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(n, d.AsSpan(0, plain.Length), d.AsSpan(plain.Length), plain);
            return plain;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>POST /api/v1/clip</c> body and <c>GET /api/v1/clip?id=</c> answer (fixture <c>clip.v1.json</c>):
/// <see cref="Data"/> is <see cref="ClipContent"/> sealed with <see cref="ClipCrypto"/>.
/// </summary>
public sealed record ClipEnvelope(int Schema, string Id, string Nonce, string Data);

/// <summary>
/// The decrypted push that carries clipboard text to a phone (fixture <c>push-clip.v1.json</c>). It has no
/// <c>title</c>, so an app from before it drops it like any push it cannot read.
/// </summary>
public sealed record ClipPushEnvelope(int Schema, string Kind, string PcId, string Id, string Nonce, string Data);

/// <summary>
/// The sealed part. <see cref="Text"/> is left out of a push when it does not fit (the phone then fetches it);
/// <see cref="Chars"/> is its length either way. <see cref="Sensitive"/>: a password manager put it on the
/// clipboard, so the receiver keeps it out of clipboard history and previews.
/// </summary>
public sealed record ClipContent(int Schema, string? Text, int Chars, DateTimeOffset Ts, bool? Sensitive = null);

public static class ClipMessages
{
    /// <summary>A sealed <see cref="ClipEnvelope"/> for the device whose token hashes to <paramref name="tokenSha256Hex"/>.</summary>
    public static ClipEnvelope Seal(string tokenSha256Hex, string id, ClipContent content, byte[]? nonce = null)
    {
        var (n, data) = ClipCrypto.Seal(ClipCrypto.Key(tokenSha256Hex), Serialize(content), nonce);
        return new ClipEnvelope(MobileProtocol.Schema, id, n, data);
    }

    /// <summary>
    /// The content of <paramref name="envelope"/>, or <c>null</c> when it is not ours, does not decrypt with this
    /// device's key, or is out of bounds (no text, longer than <see cref="ClipProtocol.MaxChars"/>).
    /// </summary>
    public static ClipContent? Open(string tokenSha256Hex, ClipEnvelope? envelope)
    {
        if (envelope is null || envelope.Schema != MobileProtocol.Schema || !ClipProtocol.IsValidId(envelope.Id)) return null;
        var plain = ClipCrypto.Open(ClipCrypto.Key(tokenSha256Hex), envelope.Nonce, envelope.Data);
        if (plain is null) return null;
        ClipContent? content;
        try { content = JsonSerializer.Deserialize<ClipContent>(plain, ClipProtocol.ContentJson); }
        catch (JsonException) { return null; }
        if (content is not { Schema: MobileProtocol.Schema, Text.Length: > 0 } || content.Text.Length > ClipProtocol.MaxChars) return null;
        return content;
    }

    /// <summary>
    /// The plaintext push for one phone, or <c>null</c> when it does not fit in one push (at most
    /// <see cref="WebPushEncryption.MaxPlaintextBytes"/>): the caller then sends it without the text.
    /// </summary>
    public static byte[]? PushPlaintext(string pcId, string tokenSha256Hex, string id, ClipContent content, byte[]? nonce = null)
    {
        var (n, data) = ClipCrypto.Seal(ClipCrypto.Key(tokenSha256Hex), Serialize(content), nonce);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ClipPushEnvelope(MobileProtocol.Schema, ClipProtocol.PushKind, pcId, id, n, data), MobileProtocol.Json);
        return bytes.Length <= WebPushEncryption.MaxPlaintextBytes ? bytes : null;
    }

    public static byte[] Serialize(ClipContent content) => JsonSerializer.SerializeToUtf8Bytes(content, ClipProtocol.ContentJson);
}

/// <summary>Bridge op payload <c>clip.set</c>: text another device sent, for the Tray to put on the clipboard.</summary>
public sealed record TrayClip(string Text, string Source, string SourceKind, bool Sensitive);

/// <summary>
/// A device the Tray can send clipboard text to: <see cref="Key"/> is <c>device:&lt;deviceId&gt;</c> for a phone
/// paired here that takes clipboard pushes, <c>pc:&lt;pcId&gt;</c> for a PC this one holds a token for.
/// </summary>
public sealed record ClipTarget(string Key, string Name, string Kind);

/// <summary><c>clip.targets</c> answer.</summary>
public sealed record ClipTargetList(IReadOnlyList<ClipTarget> Targets);

/// <summary><c>clip.send</c> request.</summary>
public sealed record ClipSendRequest(IReadOnlyList<string> Targets, string Text, bool Sensitive = false);

/// <summary>One target's outcome; <see cref="Error"/> is one of <see cref="ClipErrors"/> when it failed.</summary>
public sealed record ClipSendOutcome(string Key, string Name, bool Ok, string? Error = null);

/// <summary><c>clip.send</c> answer.</summary>
public sealed record ClipSendResult(IReadOnlyList<ClipSendOutcome> Results);

public static class ClipTargetKeys
{
    private const string DevicePrefix = "device:";
    private const string PcPrefix = "pc:";

    public static string Device(string deviceId) => DevicePrefix + deviceId;
    public static string Pc(string pcId) => PcPrefix + pcId;

    public static string? DeviceId(string key) =>
        key.StartsWith(DevicePrefix, StringComparison.Ordinal) && key.Length > DevicePrefix.Length ? key[DevicePrefix.Length..] : null;

    public static string? PcId(string key) =>
        key.StartsWith(PcPrefix, StringComparison.Ordinal) && key.Length > PcPrefix.Length ? key[PcPrefix.Length..] : null;
}

/// <summary>Error codes of <see cref="ClipSendOutcome"/> and of <c>POST /api/v1/clip</c>.</summary>
public static class ClipErrors
{
    /// <summary>Nobody is signed in to that PC (no Tray to put it on the clipboard).</summary>
    public const string NoUserSession = "no_user_session";
    /// <summary>No path reached that PC.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>That PC's Agent is older than clipboard sharing (404).</summary>
    public const string Unsupported = "unsupported";
    /// <summary>That PC no longer accepts this PC's token.</summary>
    public const string Rejected = "rejected";
    /// <summary>The push to that phone failed (distributor offline, rate limit, …).</summary>
    public const string PushFailed = "push_failed";
    /// <summary>The target is no longer paired or linked.</summary>
    public const string UnknownTarget = "unknown_target";
    /// <summary>The text is empty or longer than <see cref="ClipProtocol.MaxChars"/>.</summary>
    public const string Invalid = "invalid_clip";
    /// <summary>Any other failure.</summary>
    public const string Failed = "failed";
}
