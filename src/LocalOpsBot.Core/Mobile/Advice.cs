using LocalOpsBot.Core.Advisor;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// The reply to <c>POST /api/v1/advise</c> and the bridge op <c>advise</c> (DECISIONS M-60): the local
/// LLM's health advice about this PC, asked for from the phone app or the tray.
/// </summary>
/// <remarks>
/// A failure to produce advice is still a handled request (HTTP 200) with <c>ok: false</c>:
/// <c>error</c> is one of <see cref="AdviceErrors"/> for the app's own wording, <c>message</c> the
/// English detail. <c>summary</c> is the readings the model was given ("CPU load: 92%" …), shown as the
/// advice's basis. <c>cached</c>: handed out again within a minute rather than generated for this request.
/// The model's text is free-form and untrusted: clients show it as plain text, never as markup or links.
/// </remarks>
public sealed record AdviceResponse(
    int Schema,
    bool Ok,
    string? Text,
    string? Summary,
    string? Model,
    DateTimeOffset GeneratedAt,
    bool Cached,
    string? Error = null,
    string? Message = null)
{
    public static AdviceResponse From(AdviceReply r) => new(
        MobileProtocol.Schema,
        r.Ok,
        r.Ok ? r.Text : null,
        string.IsNullOrEmpty(r.Summary) ? null : r.Summary,
        string.IsNullOrEmpty(r.Model) ? null : r.Model,
        r.GeneratedAt,
        r.Cached,
        r.Ok ? null : r.Code ?? AdviceErrors.Failed,
        r.Ok ? null : r.Error);
}
