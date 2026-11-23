using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Push;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Files sent from a paired device to a PC (DECISIONS M-64, <c>docs/FILE_SHARE.md</c> 2a). A transfer is 1–20 files cut
/// into <see cref="ChunkBytes"/> chunks:
/// <list type="number">
/// <item><c>POST /api/v1/transfer</c> — a <see cref="ClipEnvelope"/> (id = the transfer id the device picked) sealing
/// <see cref="TransferMeta"/> with the clipboard key agreed with that device (M-62).</item>
/// <item><c>POST /api/v1/transfer/chunk?id=&amp;f=&amp;n=</c> — one chunk, sealed with the transfer key at its position
/// (<see cref="TransferCrypto"/>).</item>
/// <item><c>GET /api/v1/transfer?id=</c> — which chunks arrived (<see cref="TransferStatus"/>), to go on after a break.</item>
/// <item><c>POST /api/v1/transfer/done?id=</c> — a <see cref="ClipEnvelope"/> sealing <see cref="TransferHashes"/>; the PC
/// checks every file and hands them to the Tray, which saves them in the user's Downloads.</item>
/// </list>
/// </summary>
public static class TransferProtocol
{
    public const string Path = "/api/v1/transfer";
    public const string ChunkPath = "/api/v1/transfer/chunk";
    public const string DonePath = "/api/v1/transfer/done";

    /// <summary>Plaintext chunk size: well under Cloudflare's 100 MB request cap, and one chunk is one request body in memory.</summary>
    public const int ChunkBytes = 4 * 1024 * 1024;

    /// <summary>Largest chunk request body: a full chunk and its GCM tag.</summary>
    public const int MaxChunkBodyBytes = ChunkBytes + ClipCrypto.TagBytes;

    public const int MaxFiles = 20;
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxTransferBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>A file name as sent (before the PC cleans it up for Windows).</summary>
    public const int MaxNameChars = 1024;
    public const int MaxMimeChars = 128;

    /// <summary>How long a PC keeps a transfer that is unfinished or not saved yet (nobody signed in).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    /// <summary>Chunks of a file of <paramref name="size"/> bytes; an empty file has none.</summary>
    public static long ChunkCount(long size) => size <= 0 ? 0 : (size + ChunkBytes - 1) / ChunkBytes;

    /// <summary>Plaintext length of chunk <paramref name="n"/> of a file of <paramref name="size"/> bytes (0 when out of range).</summary>
    public static int ChunkLength(long size, long n)
    {
        if (n < 0 || n >= ChunkCount(size)) return 0;
        return (int)Math.Min(ChunkBytes, size - n * ChunkBytes);
    }

    /// <summary>
    /// <paramref name="meta"/> within bounds: 1–<see cref="MaxFiles"/> files, names 1–<see cref="MaxNameChars"/>
    /// characters, sizes 0–<see cref="MaxFileBytes"/> and at most <see cref="MaxTransferBytes"/> together.
    /// Null when fine, else what is wrong (<see cref="TransferErrors.TooLarge"/> or <see cref="TransferErrors.Invalid"/>).
    /// </summary>
    public static string? Check(TransferMeta? meta)
    {
        if (meta is not { Schema: MobileProtocol.Schema, Files: { Count: > 0 and <= MaxFiles } files }) return TransferErrors.Invalid;
        if (!ShareRelay.IsValidTargets(meta.To)) return TransferErrors.Invalid;
        foreach (var f in files)
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > MaxNameChars || f.Size < 0) return TransferErrors.Invalid;
            if (f.Mime is { Length: > MaxMimeChars }) return TransferErrors.Invalid;
            if (f.Size > MaxFileBytes) return TransferErrors.TooLarge;
        }
        return files.Sum(f => f.Size) > MaxTransferBytes ? TransferErrors.TooLarge : null;
    }
}

/// <summary>One file of a transfer as the device describes it; the PC never trusts <see cref="Name"/> as a path.</summary>
public sealed record TransferFile(string Name, long Size, string? Mime = null);

/// <summary>
/// Sealed in <c>POST /api/v1/transfer</c>. <see cref="To"/> (M-67): other phones of that PC to pass the files on to,
/// instead of this PC's Downloads.
/// </summary>
public sealed record TransferMeta(int Schema, IReadOnlyList<TransferFile> Files, DateTimeOffset Ts, IReadOnlyList<string>? To = null);

/// <summary>Sealed in <c>POST /api/v1/transfer/done</c>: lowercase hex SHA-256 of each file, in order.</summary>
public sealed record TransferHashes(int Schema, IReadOnlyList<string> Sha256);

/// <summary>
/// Answer to <c>POST</c> and <c>GET /api/v1/transfer</c>. <see cref="Have"/> holds, per file, the chunks that arrived
/// as ranges (<see cref="ChunkRanges"/>, e.g. <c>"0-127,130"</c>, empty when none).
/// </summary>
public sealed record TransferStatus(int Schema, string Id, string State, int ChunkBytes, IReadOnlyList<string> Have);

/// <summary>
/// Answer to <c>POST /api/v1/transfer/done</c>: <see cref="Saved"/> is <c>tray</c> when the signed-in user's Tray is
/// saving them now, <c>waiting</c> when nobody is signed in (kept <see cref="TransferProtocol.KeepFor"/>),
/// <c>forwarded</c> when they went on to the phones in <see cref="TransferMeta.To"/> (M-67).
/// </summary>
public sealed record TransferDone(int Schema, string Saved);

public static class TransferStates
{
    /// <summary>Chunks are still coming.</summary>
    public const string Receiving = "receiving";
    /// <summary>Every file arrived and matched its hash; waiting for, or being saved by, the Tray.</summary>
    public const string Complete = "complete";
}

/// <summary>Error codes of the transfer routes (with <see cref="ClipErrors.KeyNeeded"/> from M-62).</summary>
public static class TransferErrors
{
    public const string Invalid = "invalid_transfer";
    public const string TooLarge = "too_large";
    public const string NoSpace = "insufficient_storage";
    public const string Unknown = "unknown_transfer";
    public const string Busy = "busy";
    public const string Incomplete = "incomplete";
    public const string HashMismatch = "hash_mismatch";
    /// <summary>A chunk that doesn't open with the transfer key at its position, or has the wrong length.</summary>
    public const string BadChunk = "bad_chunk";
}

/// <summary>
/// The transfer key and chunk sealing. Transfer key = HKDF-SHA256(IKM = the clipboard key agreed with that device, salt
/// = 32 zero bytes, info = <c>"homebase transfer v1"</c> followed by the 16 bytes of the transfer id). Chunk = AES-256-GCM
/// with nonce = file index (4 bytes) ‖ chunk index (8 bytes), big-endian, no additional data; body = ciphertext ‖ tag.
/// A key per transfer makes positional nonces safe, and a chunk moved to another position doesn't open.
/// </summary>
public static class TransferCrypto
{
    private static readonly byte[] Info = "homebase transfer v1"u8.ToArray();
    private static readonly byte[] Salt = new byte[32];

    public static byte[] TransferKey(byte[] clipKey, string transferId)
    {
        if (!ClipProtocol.IsValidId(transferId)) throw new ArgumentException("A transfer id is 32 lowercase hex characters.", nameof(transferId));
        var info = new byte[Info.Length + 16];
        Info.CopyTo(info, 0);
        Convert.FromHexString(transferId).CopyTo(info, Info.Length);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, clipKey, ClipCrypto.KeyBytes, Salt, info);
    }

    public static byte[] Nonce(int file, long chunk)
    {
        var nonce = new byte[ClipCrypto.NonceBytes];
        BinaryPrimitives.WriteInt32BigEndian(nonce, file);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), chunk);
        return nonce;
    }

    public static byte[] SealChunk(byte[] transferKey, int file, long chunk, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[plaintext.Length + ClipCrypto.TagBytes];
        using var aes = new AesGcm(transferKey, ClipCrypto.TagBytes);
        aes.Encrypt(Nonce(file, chunk), plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length));
        return output;
    }

    /// <summary>The chunk's plaintext, or null when it is too short or doesn't open at that position with that key.</summary>
    public static byte[]? OpenChunk(byte[] transferKey, int file, long chunk, ReadOnlySpan<byte> sealedChunk)
    {
        if (sealedChunk.Length < ClipCrypto.TagBytes) return null;
        var plain = new byte[sealedChunk.Length - ClipCrypto.TagBytes];
        try
        {
            using var aes = new AesGcm(transferKey, ClipCrypto.TagBytes);
            aes.Decrypt(Nonce(file, chunk), sealedChunk[..plain.Length], sealedChunk[plain.Length..], plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Metadata and hashes travel as a <see cref="ClipEnvelope"/> (id = transfer id) sealed with the agreed clipboard key.</summary>
public static class TransferMessages
{
    public static ClipEnvelope SealMeta(byte[] clipKey, string transferId, TransferMeta meta, byte[]? nonce = null) =>
        Seal(clipKey, transferId, JsonSerializer.SerializeToUtf8Bytes(meta, ClipProtocol.ContentJson), nonce);

    public static ClipEnvelope SealHashes(byte[] clipKey, string transferId, TransferHashes hashes, byte[]? nonce = null) =>
        Seal(clipKey, transferId, JsonSerializer.SerializeToUtf8Bytes(hashes, ClipProtocol.ContentJson), nonce);

    /// <summary>The metadata, or null when the envelope doesn't open or isn't metadata of this schema (bounds: <see cref="TransferProtocol.Check"/>).</summary>
    public static TransferMeta? OpenMeta(byte[] clipKey, ClipEnvelope? envelope) => Open<TransferMeta>(clipKey, envelope);

    /// <summary>The hashes, or null when the envelope doesn't open or a hash isn't 64 lowercase hex characters.</summary>
    public static TransferHashes? OpenHashes(byte[] clipKey, ClipEnvelope? envelope)
    {
        var hashes = Open<TransferHashes>(clipKey, envelope);
        return hashes is { Schema: MobileProtocol.Schema, Sha256: { } list } && list.All(IsHash) ? hashes : null;
    }

    private static bool IsHash(string? h) => h is { Length: 64 } && h.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ClipEnvelope Seal(byte[] clipKey, string transferId, byte[] plain, byte[]? nonce)
    {
        var (n, data) = ClipCrypto.Seal(clipKey, plain, nonce);
        return new ClipEnvelope(MobileProtocol.Schema, transferId, n, data);
    }

    private static T? Open<T>(byte[] clipKey, ClipEnvelope? envelope) where T : class
    {
        if (envelope is null || envelope.Schema != MobileProtocol.Schema || !ClipProtocol.IsValidId(envelope.Id)) return null;
        var plain = ClipCrypto.Open(clipKey, envelope.Nonce, envelope.Data);
        if (plain is null) return null;
        try { return JsonSerializer.Deserialize<T>(plain, ClipProtocol.ContentJson); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Chunk numbers as compact ranges: <c>"0-127,130,132-140"</c> (sorted, empty for none).</summary>
public static class ChunkRanges
{
    public static string Format(IEnumerable<long> chunks)
    {
        var sb = new StringBuilder();
        long? start = null, prev = null;
        foreach (var n in chunks.Distinct().Order())
        {
            if (prev is { } p && n == p + 1) { prev = n; continue; }
            Flush();
            start = prev = n;
        }
        Flush();
        return sb.ToString();

        void Flush()
        {
            if (start is not { } s || prev is not { } e) return;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(s);
            if (e > s) sb.Append('-').Append(e);
        }
    }

    /// <summary>Upper bound on chunk indices/count accepted from a remote range string (a 2 GiB file has 512 chunks).</summary>
    public const long MaxChunkIndex = 4096;

    /// <summary>The chunk numbers in <paramref name="text"/>; null when it isn't in the format or exceeds bounds.</summary>
    public static IReadOnlyList<long>? Parse(string? text)
    {
        var result = new List<long>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (var part in text.Split(','))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!long.TryParse(part, System.Globalization.NumberStyles.None, null, out var one) || one > MaxChunkIndex) return null;
                if (result.Count >= MaxChunkIndex) return null;
                result.Add(one);
                continue;
            }
            if (!long.TryParse(part[..dash], System.Globalization.NumberStyles.None, null, out var from)
                || !long.TryParse(part[(dash + 1)..], System.Globalization.NumberStyles.None, null, out var to)
                || to < from || to > MaxChunkIndex)
                return null;
            if (result.Count + (to - from + 1) > MaxChunkIndex) return null;
            for (var n = from; n <= to; n++) result.Add(n);
        }
        return result;
    }
}

/// <summary>
/// A file name from another device made safe for Windows (DECISIONS M-64): no path, no reserved device names, no
/// control or bidirectional-override characters (which can fake an extension), no trailing dots or spaces, at most
/// 200 characters keeping the extension. Never empty.
/// </summary>
public static class TransferNames
{
    private const int MaxChars = 200;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static string Clean(string? name, string fallback = "file")
    {
        var s = name ?? "";
        // Only the last path segment: a name is never a path.
        var cut = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        if (cut >= 0) s = s[(cut + 1)..];
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsControl(c) || IsBidiControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*') continue;
            sb.Append(c);
        }
        s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0 || s.All(c => c == '.')) s = fallback;

        var dot = s.LastIndexOf('.');
        var stem = dot > 0 ? s[..dot] : s;
        var ext = dot > 0 ? s[dot..] : "";
        if (Reserved.Contains(stem.TrimEnd(' ', '.'))) stem = "_" + stem;
        if (ext.Length > 20) { stem += ext; ext = ""; }
        if (stem.Length + ext.Length > MaxChars) stem = TrimSurrogate(stem, MaxChars - ext.Length);
        return (stem + ext).TrimEnd('.', ' ') is { Length: > 0 } done ? done : fallback;
    }

    /// <summary><paramref name="name"/> if <paramref name="exists"/> says it is free, else "stem (2).ext", "stem (3).ext", …</summary>
    public static string Unique(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }

    // LRM/RLM/ALM, the embedding and override controls, and the isolates: they reorder what is shown.
    private static bool IsBidiControl(char c) => c is '‎' or '‏' or '؜' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    private static string TrimSurrogate(string s, int length)
    {
        if (length <= 0) return "";
        if (length < s.Length && char.IsHighSurrogate(s[length - 1])) length--;
        return s[..Math.Min(length, s.Length)];
    }
}

/// <summary>
/// Bridge op payload <c>transfer.ready</c> (M-64): files another device sent, checked and waiting on this PC. The Tray
/// reads each one from <see cref="TransferPipe"/>, saves it in the user's Downloads and answers <c>transfer.saved</c>.
/// </summary>
public sealed record TrayTransfer(string Id, string Source, string SourceKind, IReadOnlyList<TrayTransferFile> Files);

/// <summary>One file of <see cref="TrayTransfer"/>; <see cref="Name"/> is as the sender wrote it (the Tray cleans it).</summary>
public sealed record TrayTransferFile(int Index, string Name, long Size, string? Mime);

/// <summary>Bridge op payload <c>transfer.saved</c>: whether the Tray saved every file of <see cref="Id"/>.</summary>
public sealed record TrayTransferSaved(string Id, bool Ok, string? Error = null);

/// <summary>
/// <c>\.\pipe\Homebase.Transfer</c> (M-64): the bytes of a received file, from the Agent to the Tray, so neither the
/// JSON bridge carries base64 nor the Agent (SYSTEM) opens a path the user chose. One file per connection: the Tray
/// writes a bridge frame <see cref="TransferPipeRequest"/>, the Agent answers a frame <see cref="TransferPipeHeader"/>
/// and, when it is ok, exactly <see cref="TransferPipeHeader.Size"/> raw bytes, then closes.
/// </summary>
public static class TransferPipe
{
    public const string PipeName = "Homebase.Transfer";
}

/// <summary>
/// One connection's request. <see cref="Mode"/> <c>read</c> (default): the Agent sends a received file to the Tray.
/// <c>write</c> (M-65): the Tray sends a file it is sending — the Agent answers a header, reads exactly
/// <see cref="Size"/> raw bytes, then answers a second header with what it stored.
/// </summary>
public sealed record TransferPipeRequest(string Id, int File, string? Mode = null, long Size = 0)
{
    public const string Write = "write";
}

public sealed record TransferPipeHeader(bool Ok, long Size, string? Error = null);
