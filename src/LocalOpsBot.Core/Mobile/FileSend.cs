using System.Text.Json;
using LocalOpsBot.Core.Push;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Files sent from a PC (DECISIONS M-65, <c>docs/FILE_SHARE.md</c> 2b). The Tray hands the files to the Agent (it reads
/// them with the user's rights and streams them over <see cref="TransferPipe"/>); the Agent keeps them in its own folder
/// and sends them on:
/// <list type="bullet">
/// <item><b>To a phone</b>: a push of kind <see cref="PushKind"/> (sealed <see cref="ShareNotice"/>, the transfer id as
/// the push id). The phone then reads <c>GET /api/v1/transfer/offer?id=</c> (a <see cref="ClipEnvelope"/> sealing
/// <see cref="TransferOffer"/>: names, sizes, hashes), fetches each chunk with <c>GET /api/v1/transfer/fetch?id=&amp;f=&amp;n=</c>
/// (sealed with <see cref="TransferCrypto"/> under that phone's key) and says <c>POST /api/v1/transfer/received?id=</c>.</item>
/// <item><b>To another PC</b>: the Agent uploads with that PC's 2a routes (M-64), as a device of it.</item>
/// </list>
/// </summary>
public static class FileSendProtocol
{
    public const string OfferPath = "/api/v1/transfer/offer";
    public const string FetchPath = "/api/v1/transfer/fetch";
    public const string ReceivedPath = "/api/v1/transfer/received";

    /// <summary><c>kind</c> of the push that tells a phone files are waiting on a PC.</summary>
    public const string PushKind = "share";

    /// <summary>What a phone that fetches files from a PC lists in <c>features</c> of <c>POST /api/v1/push</c>.</summary>
    public const string Feature = "share";

    /// <summary>The first file's name in the push is cut to this many characters (the offer has them all).</summary>
    public const int MaxNameInPush = 100;

    /// <summary>Push TTL: the files wait on the PC for <see cref="TransferProtocol.KeepFor"/>.</summary>
    public const int PushTtlSeconds = 24 * 3600;
}

/// <summary>Sealed in the <c>share</c> push: how many files, how big, the first one's name — enough for a notification.</summary>
public sealed record ShareNotice(int Schema, int Count, long TotalBytes, string? Name, DateTimeOffset Ts);

/// <summary>Sealed in <c>GET /api/v1/transfer/offer</c>: the files, in order, with their SHA-256 (lowercase hex).</summary>
public sealed record TransferOffer(int Schema, IReadOnlyList<TransferOfferFile> Files, DateTimeOffset Ts);

public sealed record TransferOfferFile(string Name, long Size, string? Mime, string Sha256);

public static class FileSendMessages
{
    /// <summary>The plaintext <c>share</c> push for one phone (a <see cref="ClipPushEnvelope"/>, id = the transfer id).</summary>
    public static byte[] PushPlaintext(string pcId, byte[] clipKey, string transferId, ShareNotice notice, byte[]? nonce = null)
    {
        var fitted = notice with { Name = notice.Name is { Length: > FileSendProtocol.MaxNameInPush } n ? n[..FileSendProtocol.MaxNameInPush] + "…" : notice.Name };
        var (n2, data) = ClipCrypto.Seal(clipKey, JsonSerializer.SerializeToUtf8Bytes(fitted, ClipProtocol.ContentJson), nonce);
        return JsonSerializer.SerializeToUtf8Bytes(
            new ClipPushEnvelope(MobileProtocol.Schema, FileSendProtocol.PushKind, pcId, transferId, n2, data), MobileProtocol.Json);
    }

    public static ShareNotice? OpenNotice(byte[] clipKey, ClipPushEnvelope? push)
    {
        if (push is not { Schema: MobileProtocol.Schema, Kind: FileSendProtocol.PushKind } || !ClipProtocol.IsValidId(push.Id)) return null;
        var plain = ClipCrypto.Open(clipKey, push.Nonce, push.Data);
        if (plain is null) return null;
        try { return JsonSerializer.Deserialize<ShareNotice>(plain, ClipProtocol.ContentJson) is { Schema: MobileProtocol.Schema } n ? n : null; }
        catch (JsonException) { return null; }
    }

    public static ClipEnvelope SealOffer(byte[] clipKey, string transferId, TransferOffer offer, byte[]? nonce = null)
    {
        var (n, data) = ClipCrypto.Seal(clipKey, JsonSerializer.SerializeToUtf8Bytes(offer, ClipProtocol.ContentJson), nonce);
        return new ClipEnvelope(MobileProtocol.Schema, transferId, n, data);
    }

    public static TransferOffer? OpenOffer(byte[] clipKey, ClipEnvelope? envelope)
    {
        if (envelope is null || envelope.Schema != MobileProtocol.Schema || !ClipProtocol.IsValidId(envelope.Id)) return null;
        var plain = ClipCrypto.Open(clipKey, envelope.Nonce, envelope.Data);
        if (plain is null) return null;
        try { return JsonSerializer.Deserialize<TransferOffer>(plain, ClipProtocol.ContentJson) is { Schema: MobileProtocol.Schema } o ? o : null; }
        catch (JsonException) { return null; }
    }
}

// ── Tray ↔ Agent (bridge ops files.*, and the write mode of Homebase.Transfer) ────────────────────────────────────

/// <summary>
/// <c>files.begin</c>: the Tray is about to hand these files to the Agent for <see cref="Targets"/>
/// (<see cref="ClipTargetKeys"/>). Answered <see cref="FilesBeginResult"/>; then the Tray writes each file over
/// <see cref="TransferPipe"/> (mode <c>write</c>) and sends <c>files.go</c>.
/// </summary>
public sealed record FilesBeginRequest(IReadOnlyList<string> Targets, IReadOnlyList<TransferFile> Files);

/// <summary><see cref="Error"/>: <see cref="TransferErrors.TooLarge"/>, <see cref="TransferErrors.NoSpace"/>, <see cref="TransferErrors.Invalid"/>.</summary>
public sealed record FilesBeginResult(string? Id, string? Error = null);

/// <summary><c>files.go</c> / <c>files.cancel</c>.</summary>
public sealed record FilesIdRequest(string Id);

/// <summary>
/// Bridge push <c>files.progress</c>: where the files stand for one target. Final states: <see cref="FileSendStates.Sent"/>
/// (a PC has them), <see cref="FileSendStates.Received"/> (a phone fetched them), <see cref="FileSendStates.Failed"/>.
/// </summary>
public sealed record FilesProgress(string Id, string Key, string Name, string Kind, string State, long Sent, long Total, string? Error = null);

/// <summary>
/// The "app" of file entries in the PC notification center (DECISIONS M-64, M-66): what came in and what went out. The
/// Tray's "files" filter picks entries by these.
/// </summary>
public static class TransferHistory
{
    public const string Received = "받은 파일";
    public const string Sent = "보낸 파일";

    public static bool IsFiles(string? app) => app is Received or Sent;
}

public static class FileSendStates
{
    /// <summary>Uploading to a PC.</summary>
    public const string Sending = "sending";
    /// <summary>The other PC has them (saved by its Tray, or kept until someone signs in there).</summary>
    public const string Sent = "sent";
    /// <summary>The phone got the push; it fetches the files when it can (or when the user taps).</summary>
    public const string Notified = "notified";
    /// <summary>The phone fetched every file.</summary>
    public const string Received = "received";
    public const string Failed = "failed";
}
