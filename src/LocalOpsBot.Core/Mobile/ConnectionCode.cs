using System.Security.Cryptography;
using System.Text;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// What a 24-word connection code carries (DECISIONS M-30): enough for another PC to find this one
/// on the LAN or tailnet and pair with the one-time code — not the paths themselves.
/// </summary>
public sealed record ConnectionCodeData(byte[] PcTag, byte[]? LanPinPrefix, int Port, byte[] Secret)
{
    /// <summary>The pairing code to send (<c>PairRequest.Code</c>): the secret as base64url, as in the QR.</summary>
    public string PairingCode => PairingService.Base64Url(Secret);

    public bool MatchesPcId(string pcId) => ConnectionCode.Tag(pcId).AsSpan().SequenceEqual(PcTag);

    /// <summary>Whether a certificate with this SHA-256 (hex) is the one the code names.</summary>
    public bool MatchesPin(string sha256Hex) =>
        LanPinPrefix is { } prefix && sha256Hex.Length >= 16
        && Convert.FromHexString(sha256Hex[..16]).AsSpan().SequenceEqual(prefix);
}

/// <summary>
/// 24 BIP39 English words ↔ 32 bytes: version (1) · SHA-256(pcId)[0..4] · LAN certificate
/// SHA-256[0..8] (zeros without LAN) · port (big-endian) · 16-byte pairing secret · reserved (0).
/// Encoding follows BIP39: 256 bits plus the first byte of their SHA-256 as an 8-bit checksum,
/// cut into 24 groups of 11 bits. Built by the Tray from the current pairing offer; the code lives
/// as long as that offer (5 minutes, single use).
/// </summary>
public static class ConnectionCode
{
    public const int WordCount = 24;
    private const byte Version = 1;

    private static readonly Lazy<string[]> Words = new(LoadWords);
    private static readonly Lazy<Dictionary<string, int>> Index = new(() =>
        Words.Value.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i, StringComparer.Ordinal));

    public static IReadOnlyList<string> WordList => Words.Value;

    public static byte[] Tag(string pcId) => SHA256.HashData(Encoding.UTF8.GetBytes(pcId))[..4];

    /// <summary>The code for <paramref name="offer"/>; null when its code isn't the 16-byte form.</summary>
    public static string? Encode(PairingOffer offer)
    {
        byte[] secret;
        try { secret = Push.Base64Url.Decode(offer.Code); }
        catch (FormatException) { return null; }
        if (secret.Length != 16) return null;

        var lan = offer.Endpoints.FirstOrDefault(e => e.Transport == MobileTransport.Lan && e.CertSha256 is { Length: 64 });
        var port = lan?.Port ?? offer.Endpoints.FirstOrDefault(e => e.Transport == MobileTransport.Tailscale)?.Port ?? offer.Port;

        var data = new byte[32];
        data[0] = Version;
        Tag(offer.PcId).CopyTo(data, 1);
        if (lan is not null) Convert.FromHexString(lan.CertSha256![..16]).CopyTo(data, 5);
        data[13] = (byte)(port >> 8);
        data[14] = (byte)port;
        secret.CopyTo(data, 15);
        return EncodeBytes(data);
    }

    /// <summary>
    /// Reads a pasted code: any whitespace, commas or numbering between words, any letter case.
    /// Null when it isn't 24 known words, the checksum fails or the version is unknown.
    /// </summary>
    public static ConnectionCodeData? TryDecode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(t => t.Split(',', '.', ';'))
            .Select(t => t.Trim())
            .Where(t => t.Length > 0 && !t.All(char.IsDigit)) // "1." numbering from a list
            .ToList();
        if (words.Count != WordCount) return null;

        var bits = new bool[WordCount * 11];
        for (var i = 0; i < WordCount; i++)
        {
            if (!Index.Value.TryGetValue(words[i], out var n)) return null;
            for (var b = 0; b < 11; b++) bits[i * 11 + b] = (n >> (10 - b) & 1) == 1;
        }
        var data = new byte[32];
        for (var i = 0; i < 256; i++) if (bits[i]) data[i / 8] |= (byte)(0x80 >> (i % 8));
        byte checksum = 0;
        for (var i = 0; i < 8; i++) if (bits[256 + i]) checksum |= (byte)(0x80 >> i);
        if (SHA256.HashData(data)[0] != checksum || data[0] != Version) return null;

        var pin = data[5..13];
        return new ConnectionCodeData(
            data[1..5],
            pin.All(b => b == 0) ? null : pin,
            data[13] << 8 | data[14],
            data[15..31]);
    }

    /// <summary>Plain BIP39 for 32 bytes: 24 words with the 8-bit SHA-256 checksum.</summary>
    public static string EncodeBytes(byte[] data)
    {
        if (data.Length != 32) throw new ArgumentException("Exactly 32 bytes.", nameof(data));
        return string.Join(' ', ToWords(data));
    }

    private static IEnumerable<string> ToWords(byte[] data)
    {
        var checksum = SHA256.HashData(data)[0];
        var bits = new bool[264];
        for (var i = 0; i < 256; i++) bits[i] = (data[i / 8] & (0x80 >> (i % 8))) != 0;
        for (var i = 0; i < 8; i++) bits[256 + i] = (checksum & (0x80 >> i)) != 0;
        for (var w = 0; w < WordCount; w++)
        {
            var n = 0;
            for (var b = 0; b < 11; b++) n = n << 1 | (bits[w * 11 + b] ? 1 : 0);
            yield return Words.Value[n];
        }
    }

    private static string[] LoadWords()
    {
        using var stream = typeof(ConnectionCode).Assembly.GetManifestResourceStream("LocalOpsBot.Core.Mobile.bip39-english.txt")
            ?? throw new InvalidOperationException("BIP39 wordlist resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = reader.ReadToEnd().Split('\n').Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
        if (words.Length != 2048) throw new InvalidOperationException($"BIP39 wordlist has {words.Length} words, expected 2048.");
        return words;
    }
}
