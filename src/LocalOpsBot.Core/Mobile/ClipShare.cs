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
/// The text is always encrypted end to end (<see cref="ClipCrypto"/>) with a key that device and that PC agreed
/// once by ECDH (<c>POST /api/v1/clip/key</c>, <see cref="ClipKeys"/>). Only public keys cross the network, so
/// the Cloudflare edge — which ends TLS and sees every bearer token — cannot derive it, and neither can another
/// paired PC, which shares a phone's push keys. (An edge that actively rewrote that one exchange could; reading
/// traffic is not enough.)
/// </summary>
public static class ClipProtocol
{
    public const string Path = "/api/v1/clip";

    /// <summary>Where a device and a PC agree their clipboard key (<see cref="ClipKeyExchange"/>).</summary>
    public const string KeyPath = "/api/v1/clip/key";

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
/// AES-256-GCM with the key a device and a PC agreed (<see cref="ClipKeys"/>): HKDF-SHA256 (RFC 5869) over the
/// raw P-256 ECDH secret (the 32-byte x coordinate), salt 32 zero bytes, info <c>"homebase clip v1"</c> followed by
/// the device's and then the PC's 65-byte uncompressed public key. A 12-byte random nonce per message; the data is
/// the ciphertext followed by the 16-byte tag, as Java's <c>AES/GCM/NoPadding</c> writes it. No additional data.
/// </summary>
public static class ClipCrypto
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int PublicKeyBytes = 65;

    private static readonly byte[] Info = "homebase clip v1"u8.ToArray();
    private static readonly byte[] Salt = new byte[32];

    /// <summary>
    /// The key from <paramref name="own"/> (this side's private key) and the other side's public key. Both sides
    /// pass the two public keys in the same order — the device's, then the PC's — so they derive the same key.
    /// Throws <see cref="ArgumentException"/> when <paramref name="peerPublic"/> is not a P-256 point.
    /// </summary>
    public static byte[] AgreedKey(ECDiffieHellman own, ReadOnlySpan<byte> peerPublic, ReadOnlySpan<byte> devicePublic, ReadOnlySpan<byte> pcPublic)
    {
        using var peer = WebPushEncryption.ImportPublic(peerPublic);
        var secret = own.DeriveRawSecretAgreement(peer.PublicKey);
        var info = new byte[Info.Length + devicePublic.Length + pcPublic.Length];
        Info.CopyTo(info, 0);
        devicePublic.CopyTo(info.AsSpan(Info.Length));
        pcPublic.CopyTo(info.AsSpan(Info.Length + devicePublic.Length));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeyBytes, Salt, info);
    }

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
/// <c>POST /api/v1/clip/key</c> body (the device's public key) and answer (the PC's): a P-256 point, 65 bytes
/// uncompressed, base64url. Each side makes a fresh key pair for the exchange and keeps only the agreed key.
/// </summary>
public sealed record ClipKeyExchange(int Schema, string Pub);

/// <summary>The ECDH exchange behind the clipboard key (DECISIONS M-62).</summary>
public static class ClipKeys
{
    /// <summary>
    /// The PC's answer to a device's <c>POST /api/v1/clip/key</c>: its public key and the agreed key, or null when the
    /// device's key is not a P-256 point. <paramref name="pcKey"/> is for tests; a fresh pair is made otherwise.
    /// </summary>
    public static (ClipKeyExchange Answer, byte[] Key)? Answer(ClipKeyExchange? request, ECDiffieHellman? pcKey = null)
    {
        if (request is not { Schema: MobileProtocol.Schema, Pub.Length: > 0 }) return null;
        var ownKey = pcKey is null;
        pcKey ??= ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var devicePublic = Base64Url.Decode(request.Pub);
            if (devicePublic.Length != ClipCrypto.PublicKeyBytes) return null;
            var pcPublic = WebPushEncryption.ExportUncompressed(pcKey);
            var key = ClipCrypto.AgreedKey(pcKey, devicePublic, devicePublic, pcPublic);
            return (new ClipKeyExchange(MobileProtocol.Schema, Base64Url.Encode(pcPublic)), key);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            return null;
        }
        finally
        {
            if (ownKey) pcKey.Dispose();
        }
    }

    /// <summary>A device's side (a PC sending to another PC): a fresh key pair and the request to send.</summary>
    public static (ECDiffieHellman Key, ClipKeyExchange Request) Begin()
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (key, new ClipKeyExchange(MobileProtocol.Schema, Base64Url.Encode(WebPushEncryption.ExportUncompressed(key))));
    }

    /// <summary>The agreed key from the PC's answer, or null when it is not a P-256 point.</summary>
    public static byte[]? Finish(ECDiffieHellman deviceKey, ClipKeyExchange? answer)
    {
        if (answer is not { Schema: MobileProtocol.Schema, Pub.Length: > 0 }) return null;
        try
        {
            var pcPublic = Base64Url.Decode(answer.Pub);
            if (pcPublic.Length != ClipCrypto.PublicKeyBytes) return null;
            return ClipCrypto.AgreedKey(deviceKey, pcPublic, WebPushEncryption.ExportUncompressed(deviceKey), pcPublic);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
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
/// <see cref="To"/> (phone → PC only, M-67): other phones of that PC to pass it on to, instead of this PC's clipboard.
/// <see cref="From"/> (PC → phone only): the phone it came from, when this PC passed it on.
/// </summary>
public sealed record ClipContent(int Schema, string? Text, int Chars, DateTimeOffset Ts, bool? Sensitive = null,
    IReadOnlyList<string>? To = null, string? From = null);

public static class ClipMessages
{
    /// <summary>A <see cref="ClipEnvelope"/> sealed with the agreed <paramref name="key"/>.</summary>
    public static ClipEnvelope Seal(byte[] key, string id, ClipContent content, byte[]? nonce = null)
    {
        var (n, data) = ClipCrypto.Seal(key, Serialize(content), nonce);
        return new ClipEnvelope(MobileProtocol.Schema, id, n, data);
    }

    /// <summary>
    /// The content of <paramref name="envelope"/>, or <c>null</c> when it is not ours, does not decrypt with the
    /// agreed <paramref name="key"/>, or is out of bounds (no text, longer than <see cref="ClipProtocol.MaxChars"/>).
    /// </summary>
    public static ClipContent? Open(byte[] key, ClipEnvelope? envelope)
    {
        if (envelope is null || envelope.Schema != MobileProtocol.Schema || !ClipProtocol.IsValidId(envelope.Id)) return null;
        var plain = ClipCrypto.Open(key, envelope.Nonce, envelope.Data);
        if (plain is null) return null;
        ClipContent? content;
        try { content = JsonSerializer.Deserialize<ClipContent>(plain, ClipProtocol.ContentJson); }
        catch (JsonException) { return null; }
        if (content is not { Schema: MobileProtocol.Schema, Text.Length: > 0 } || content.Text.Length > ClipProtocol.MaxChars) return null;
        if (!ShareRelay.IsValidTargets(content.To)) return null;
        return content;
    }

    /// <summary>
    /// The plaintext push for one phone, or <c>null</c> when it does not fit in one push (at most
    /// <see cref="WebPushEncryption.MaxPlaintextBytes"/>): the caller then sends it without the text.
    /// </summary>
    public static byte[]? PushPlaintext(string pcId, byte[] key, string id, ClipContent content, byte[]? nonce = null)
    {
        var (n, data) = ClipCrypto.Seal(key, Serialize(content), nonce);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ClipPushEnvelope(MobileProtocol.Schema, ClipProtocol.PushKind, pcId, id, n, data), MobileProtocol.Json);
        return bytes.Length <= WebPushEncryption.MaxPlaintextBytes ? bytes : null;
    }

    public static byte[] Serialize(ClipContent content) => JsonSerializer.SerializeToUtf8Bytes(content, ClipProtocol.ContentJson);
}

/// <summary>
/// Bridge op payload <c>clip.set</c>: text another device sent, for the Tray to put on the clipboard. The Tray answers
/// <c>clip.done {id, ok}</c> with the frame's <c>id</c> once it has (or could not), so the sender hears the truth.
/// </summary>
public sealed record TrayClip(string Text, string Source, string SourceKind, bool Sensitive);

/// <summary>Bridge op payload <c>clip.done</c>: whether the Tray put <c>clip.set</c> <see cref="Id"/> on the clipboard.</summary>
public sealed record TrayClipDone(string Id, bool Ok);

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
    /// <summary>The text is empty or longer than <see cref="ClipProtocol.MaxChars"/>, or doesn't open with the agreed key.</summary>
    public const string Invalid = "invalid_clip";
    /// <summary>That PC has no clipboard key for this device: agree one (<c>POST /api/v1/clip/key</c>) and send again.</summary>
    public const string KeyNeeded = "clip_key_needed";
    /// <summary>The public key sent to <c>POST /api/v1/clip/key</c> is not a P-256 point.</summary>
    public const string InvalidKey = "invalid_key";
    /// <summary>That PC's clipboard stayed busy (another app held it): nothing was pasted, send again.</summary>
    public const string ClipboardBusy = "clipboard_busy";
    /// <summary>Any other failure.</summary>
    public const string Failed = "failed";
}
