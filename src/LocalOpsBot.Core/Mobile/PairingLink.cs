using System.Text;
using System.Text.Json;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// The pairing QR as an https link (DECISIONS M-20):
/// <c>https://jeiel85.github.io/homebase/pair/#&lt;base64url(JSON)&gt;</c>.
/// <list type="bullet">
/// <item>App installed → the link opens the app's pairing screen (App Link, or the page's
/// "앱에서 열기" button).</item>
/// <item>App missing → the phone's camera opens the install guide page instead of showing raw JSON.</item>
/// </list>
/// The offer sits in the fragment, which browsers never send to the server, so the one-time code
/// stays on the phone. The JSON drops the v1 <c>hosts</c>/<c>port</c> fields: apps that read
/// links all understand <c>endpoints</c>.
/// </summary>
public static class PairingLink
{
    public const string Prefix = "https://jeiel85.github.io/homebase/pair/#";

    /// <summary>What the link carries (the offer without the legacy Tailscale-only fields).</summary>
    public sealed record Payload(
        int Hb,
        string PcId,
        string PcName,
        string Code,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<MobileEndpoint> Endpoints);

    public static string Build(PairingOffer offer)
    {
        var payload = new Payload(offer.Hb, offer.PcId, offer.PcName, offer.Code, offer.ExpiresAt, offer.Endpoints);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, MobileProtocol.Json);
        return Prefix + PairingService.Base64Url(json);
    }

    /// <summary>The offer JSON inside a pairing link, or <c>null</c> when <paramref name="link"/> isn't one.</summary>
    public static string? DecodeJson(string link)
    {
        if (!link.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var b64 = link[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { return null; }
    }
}
