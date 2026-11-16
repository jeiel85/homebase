using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Core.Push;

/// <summary>
/// <c>POST /api/v1/push</c> body: the phone's UnifiedPush endpoint and its RFC 8291 subscription
/// keys, both base64url without padding (<c>docs/MOBILE_PROTOCOL.md</c>, fixture
/// <c>push-subscription.v1.json</c>). <see cref="MutedCategories"/> lists the notification
/// categories the phone does not want (DECISIONS M-25); absent means everything, so apps from
/// before M-25 keep receiving all of them.
/// </summary>
public sealed record PushSubscriptionRequest(
    int Schema, string Endpoint, string P256dh, string Auth, IReadOnlyList<string>? MutedCategories = null);

/// <summary>Answer to <c>POST</c>/<c>DELETE /api/v1/push</c>.</summary>
public sealed record PushSubscriptionResponse(int Schema, string DeviceId, bool Registered);

/// <summary>A stored subscription (in <c>state.json</c>, next to the paired device it belongs to).</summary>
public sealed record PushSubscription(
    string Endpoint, string P256dh, string Auth, DateTimeOffset UpdatedAt, IReadOnlyList<string>? MutedCategories = null)
{
    /// <summary>Whether this phone asked not to receive <paramref name="category"/>. The Tray test always goes through.</summary>
    public bool Mutes(string category) =>
        category != PushCategories.Test && MutedCategories is { Count: > 0 } muted && muted.Contains(category, StringComparer.Ordinal);
}

/// <summary>Notification categories a phone can turn off (DECISIONS M-25).</summary>
public static class PushCategories
{
    /// <summary>Tray "send test" — never muted, so the user can always check the path works.</summary>
    public const string Test = "test";

    public const int MaxMuted = 32;
    public const int MaxLength = 64;

    /// <summary>Sorted, distinct, or <c>null</c> when nothing is muted — so equal choices compare equal.</summary>
    public static IReadOnlyList<string>? Normalize(IReadOnlyList<string>? muted) =>
        muted is null || muted.Count == 0
            ? null
            : muted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static bool SameMuted(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (Normalize(a) ?? []).SequenceEqual(Normalize(b) ?? [], StringComparer.Ordinal);

    /// <summary>A category id: 1–64 of <c>a-z 0-9 _ -</c>.</summary>
    public static bool IsValid(string? category) =>
        !string.IsNullOrEmpty(category) && category.Length <= MaxLength
        && category.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

/// <param name="TokenSha256">
/// Lowercase hex SHA-256 of the device's bearer token: the key that signs endpoint announcements
/// (<see cref="PushEndpoints"/>), which only this PC and that phone know.
/// </param>
public sealed record PushTarget(string DeviceId, string DeviceName, PushSubscription Subscription, string? TokenSha256 = null);

/// <summary>
/// Decrypted push that carries this PC's current paths instead of a notification (DECISIONS M-23).
/// <see cref="Data"/> is base64url of a UTF-8 <see cref="PushEndpointsData"/>; <see cref="Mac"/> is
/// base64url HMAC-SHA256 over those bytes, keyed with the raw SHA-256 of the device's token. Every
/// paired PC shares the phone's push keys, so the MAC is what stops one PC from rewriting another's
/// paths (and harvesting its token). Fixture <c>push-endpoints.v1.json</c>.
/// </summary>
public sealed record PushEndpointsEnvelope(int Schema, string Kind, string PcId, string Data, string Mac);

/// <summary>The signed part of <see cref="PushEndpointsEnvelope"/>.</summary>
public sealed record PushEndpointsData(int Schema, string PcId, DateTimeOffset Ts, IReadOnlyList<MobileEndpoint> Endpoints);

public static class PushEndpoints
{
    public const string Kind = "endpoints";

    /// <summary>Longest PC id <see cref="FitToOnePush"/> budgets for (ids are 32 hex characters).</summary>
    public const int MaxPcIdLength = 64;

    // Size stand-ins for FitToOnePush: the longest PC id, a timestamp with all 7 fraction digits,
    // and any key (the MAC is 32 bytes whatever the key).
    private static readonly string BudgetPcId = new('0', MaxPcIdLength);
    private static readonly DateTimeOffset BudgetTs = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);
    private static readonly byte[] BudgetKey = new byte[32];

    /// <summary>
    /// The plaintext push for one device, signed with that device's token hash. A list too long for
    /// one push is cut down by <see cref="FitToOnePush"/> rather than failing the whole update.
    /// </summary>
    public static byte[] Serialize(string pcId, IReadOnlyList<MobileEndpoint> endpoints, DateTimeOffset ts, string tokenSha256Hex)
    {
        var fitted = FitToOnePush(endpoints);
        // An empty list would tell the phone this PC has no paths at all.
        if (fitted.Count == 0 && endpoints.Count > 0)
            throw new ArgumentException("No single endpoint fits in one push.", nameof(endpoints));
        var bytes = Build(pcId, fitted, ts, Convert.FromHexString(tokenSha256Hex));
        if (bytes.Length > WebPushEncryption.MaxPlaintextBytes)
            throw new ArgumentException($"Endpoint list is too long for one push ({bytes.Length} bytes).", nameof(endpoints));
        return bytes;
    }

    /// <summary>
    /// The paths that fit in one push (many LAN adapters or long host names can overflow it), in their
    /// original order. When they don't all fit, the first path of each transport (the preferred one)
    /// goes in first, then as many of the rest, in order, as still fit. The result does not depend on
    /// the PC id or the time (both are budgeted at their longest), so a caller can tell in advance
    /// whether a list will be cut. Returns <paramref name="endpoints"/> itself when everything fits.
    /// </summary>
    public static IReadOnlyList<MobileEndpoint> FitToOnePush(IReadOnlyList<MobileEndpoint> endpoints)
    {
        if (Fits(endpoints)) return endpoints;

        var firsts = new List<int>();
        var rest = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < endpoints.Count; i++)
            (seen.Add(endpoints[i].Transport) ? firsts : rest).Add(i);

        var keep = new bool[endpoints.Count];
        foreach (var i in firsts.Concat(rest))
        {
            keep[i] = true;
            // A path that doesn't fit is skipped; a shorter one later in the list may still fit.
            if (!Fits(Kept(endpoints, keep))) keep[i] = false;
        }
        return Kept(endpoints, keep);
    }

    private static List<MobileEndpoint> Kept(IReadOnlyList<MobileEndpoint> endpoints, bool[] keep) =>
        endpoints.Where((_, i) => keep[i]).ToList();

    private static bool Fits(IReadOnlyList<MobileEndpoint> endpoints) =>
        Build(BudgetPcId, endpoints, BudgetTs, BudgetKey).Length <= WebPushEncryption.MaxPlaintextBytes;

    private static byte[] Build(string pcId, IReadOnlyList<MobileEndpoint> endpoints, DateTimeOffset ts, byte[] key)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new PushEndpointsData(MobileProtocol.Schema, pcId, ts, endpoints), MobileProtocol.Json);
        var mac = System.Security.Cryptography.HMACSHA256.HashData(key, data);
        return JsonSerializer.SerializeToUtf8Bytes(
            new PushEndpointsEnvelope(MobileProtocol.Schema, Kind, pcId, Base64Url.Encode(data), Base64Url.Encode(mac)),
            MobileProtocol.Json);
    }
}

/// <summary>
/// The decrypted push payload (UTF-8 JSON, fixture <c>push-message.v1.json</c>). The app shows one
/// notification per message and files it under a channel per PC and <see cref="Source"/>
/// (the Windows app for toasts, the alert kind otherwise) so the user can mute each separately.
/// </summary>
public sealed record PushMessage(
    int Schema,
    string Id,
    string PcId,
    string PcName,
    string Category,
    string Priority,
    string Title,
    string? Body,
    string Source,
    int Count,
    DateTimeOffset Ts);

public static class PushPriority
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
    public const string Recovery = "recovery";
}

public static class PushPayload
{
    public const int MaxEndpointBytes = 1000;

    private const string Ellipsis = "…";

    /// <summary>
    /// Serializes <paramref name="message"/> to at most <see cref="WebPushEncryption.MaxPlaintextBytes"/>
    /// UTF-8 bytes, shortening the body first and then the title.
    /// </summary>
    public static byte[] Serialize(PushMessage message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, MobileProtocol.Json);
        if (bytes.Length <= WebPushEncryption.MaxPlaintextBytes) return bytes;

        var over = bytes.Length - WebPushEncryption.MaxPlaintextBytes;
        var body = message.Body ?? "";
        // JSON escaping can make a char cost up to 6 bytes; shrink until it fits.
        while (body.Length > 0)
        {
            var cut = Math.Max(1, over / 3);
            body = Truncate(body, Math.Max(0, body.Length - cut));
            bytes = JsonSerializer.SerializeToUtf8Bytes(message with { Body = body + Ellipsis }, MobileProtocol.Json);
            if (bytes.Length <= WebPushEncryption.MaxPlaintextBytes) return bytes;
            over = bytes.Length - WebPushEncryption.MaxPlaintextBytes;
        }

        var title = message.Title;
        while (title.Length > 0)
        {
            title = Truncate(title, Math.Max(0, title.Length - Math.Max(1, over / 3)));
            bytes = JsonSerializer.SerializeToUtf8Bytes(message with { Body = null, Title = title + Ellipsis }, MobileProtocol.Json);
            if (bytes.Length <= WebPushEncryption.MaxPlaintextBytes) return bytes;
            over = bytes.Length - WebPushEncryption.MaxPlaintextBytes;
        }
        throw new ArgumentException("Push message does not fit even without title and body.", nameof(message));
    }

    /// <summary>Validates a subscription from the phone; returns an error message or <c>null</c>.</summary>
    public static string? Validate(PushSubscriptionRequest? request)
    {
        if (request is null) return "Invalid subscription.";
        if (request.Schema != MobileProtocol.Schema) return $"Unsupported schema {request.Schema}.";
        if (string.IsNullOrWhiteSpace(request.Endpoint) || Encoding.UTF8.GetByteCount(request.Endpoint) > MaxEndpointBytes)
            return "Endpoint is missing or too long.";
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
            return "Endpoint must be an https URL.";
        try
        {
            WebPushEncryption.ValidateSubscriptionKeys(Base64Url.Decode(request.P256dh), Base64Url.Decode(request.Auth));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return "Subscription keys are invalid.";
        }
        if (request.MutedCategories is { } muted
            && (muted.Count > PushCategories.MaxMuted || !muted.All(PushCategories.IsValid)))
            return "Muted categories are invalid.";
        return null;
    }

    private static string Truncate(string s, int length)
    {
        if (length <= 0) return "";
        // Don't split a surrogate pair.
        if (length < s.Length && char.IsHighSurrogate(s[length - 1])) length--;
        return s[..length];
    }
}

/// <summary>Base64url without padding (RFC 4648 §5), the encoding Web Push uses for keys.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) throw new FormatException("Empty base64url value.");
        var s = text.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", 0 => s, _ => throw new FormatException("Bad base64url length.") };
        return Convert.FromBase64String(s);
    }
}

/// <summary>
/// <see cref="LocalOpsBot.Core.Delivery.OutboundNotification.Metadata"/> keys for relayed notifications
/// (DECISIONS M-28): the push skips the device that sent it and the phones that already got it.
/// </summary>
public static class PushRelay
{
    public const string OriginDeviceKey = "originDeviceId";

    /// <summary>Comma-separated <see cref="LocalOpsBot.Core.Mobile.PushHash"/> values.</summary>
    public const string SkipHashesKey = "skipPushHashes";

    public static bool Skips(PushTarget target, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return false;
        if (metadata.TryGetValue(OriginDeviceKey, out var origin) && origin == target.DeviceId) return true;
        if (!metadata.TryGetValue(SkipHashesKey, out var hashes) || string.IsNullOrEmpty(hashes)) return false;
        var mine = LocalOpsBot.Core.Mobile.PushHash.Of(target.Subscription.Endpoint);
        return hashes.Split(',').Contains(mine, StringComparer.Ordinal);
    }
}
