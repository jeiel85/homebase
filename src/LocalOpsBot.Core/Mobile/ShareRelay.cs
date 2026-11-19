namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Phone ↔ phone through a PC (DECISIONS M-67, <c>docs/SHARE_CONVENIENCE.md</c> §4.8, §9). A phone names other phones
/// paired with the same PC in <c>to</c> of the clipboard text (<see cref="ClipContent.To"/>) or of the transfer description
/// (<see cref="TransferMeta.To"/>); the PC opens it with the sender's key and sends it on as its own PC → phone push (M-62)
/// or 2b send (M-65), sealed with each receiver's key and carrying the sender's name in <c>from</c>. The PC sees the
/// content in between: it is the user's own PC, as for relayed notifications (M-28).
/// </summary>
public static class ShareRelay
{
    /// <summary>Answer: <see cref="SharePhones"/>. A PC that answers 404 is too old to pass anything on.</summary>
    public const string PhonesPath = "/api/v1/share/phones";

    /// <summary><see cref="TransferDone.Saved"/> when the PC took the files for other phones instead of its Tray.</summary>
    public const string Forwarded = "forwarded";

    /// <summary>Phones one message or transfer is passed on to, at most.</summary>
    public const int MaxTargets = 10;

    /// <summary>
    /// Whether <paramref name="to"/> is a list the PC can act on: absent, or 1–<see cref="MaxTargets"/> device ids (letters,
    /// digits, <c>-</c> and <c>_</c>, up to 64 characters).
    /// </summary>
    public static bool IsValidTargets(IReadOnlyList<string>? to) =>
        to is null || (to.Count is > 0 and <= MaxTargets && to.All(IsValidDeviceId));

    private static bool IsValidDeviceId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary><c>GET /api/v1/share/phones</c>: the other phones paired with this PC that it can pass things on to.</summary>
public sealed record SharePhones(int Schema, IReadOnlyList<SharePhone> Phones);

/// <summary>
/// <see cref="Clip"/>: it takes clipboard text (its app reads <c>clip</c> pushes and agreed a key with this PC);
/// <see cref="Files"/>: it fetches files (<c>share</c>).
/// </summary>
public sealed record SharePhone(string DeviceId, string Name, bool Clip, bool Files);

/// <summary>
/// Answer to <c>POST /api/v1/clip</c>. <see cref="Forwarded"/> is there when the text was for other phones
/// (<see cref="ClipContent.To"/>): one outcome each, in the order asked.
/// </summary>
public sealed record ClipAccepted(int Schema, bool Accepted, IReadOnlyList<ClipForwardOutcome>? Forwarded = null);

/// <summary><see cref="Error"/>: <see cref="ClipErrors"/> (<c>unknown_target</c>, <c>push_failed</c>, …).</summary>
public sealed record ClipForwardOutcome(string DeviceId, bool Ok, string? Error = null);
