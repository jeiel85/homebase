using System.Text.Json;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Commands a phone asks a PC to carry out (DECISIONS M-69, <c>docs/REMOTE_COMMANDS.md</c>): a fixed list the PC turns on
/// one by one, never a shell. The request is sealed with the key the phone agreed with this PC (M-62) — the Bearer token
/// alone, which the Cloudflare edge sees, cannot make or replay one — carries a fresh id and the phone's clock, and is
/// carried out at most once.
/// </summary>
public static class RemoteCommandProtocol
{
    /// <summary><c>GET</c>: <see cref="CommandList"/>. <c>POST</c>: a sealed <see cref="CommandRequest"/> → <see cref="CommandResult"/>.</summary>
    public const string Path = "/api/v1/commands";

    /// <summary><c>POST</c>: a sealed <see cref="CommandRequest"/> naming what to call off (a restart under way).</summary>
    public const string CancelPath = "/api/v1/commands/cancel";

    /// <summary>A request whose <c>ts</c> is further than this from the PC's clock is refused.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(2);

    /// <summary>Request ids are remembered this long (longer than <see cref="MaxSkew"/> both ways), so none runs twice.</summary>
    public static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(10);

    /// <summary>Requests one device may make in a minute.</summary>
    public const int PerMinute = 10;

    /// <summary>A restart waits this long, with a countdown and [Cancel] on the PC.</summary>
    public const int RestartDelaySeconds = 60;

    /// <summary>The "app" of remote command entries in the PC notification center (its filter picks them by this).</summary>
    public const string HistoryApp = "원격 명령";
}

/// <summary>The commands there are. A custom one is <c>custom:&lt;id&gt;</c>, registered on the PC by its user.</summary>
public static class RemoteCommandNames
{
    public const string Lock = "lock";
    public const string ScreenOff = "screen_off";
    public const string Mute = "mute";
    public const string VolumeUp = "volume_up";
    public const string VolumeDown = "volume_down";
    public const string PlayPause = "media_play_pause";
    public const string Next = "media_next";
    public const string Previous = "media_previous";
    public const string Restart = "restart";
    public const string Sleep = "sleep";
    public const string CustomPrefix = "custom:";

    /// <summary>Run by the Tray in the signed-in user's session.</summary>
    public static readonly IReadOnlyList<string> Session = [Lock, ScreenOff, Mute, VolumeUp, VolumeDown, PlayPause, Next, Previous];

    /// <summary>Run by the Agent; the phone asks the user to unlock it first.</summary>
    public static readonly IReadOnlyList<string> System = [Restart, Sleep];

    public static bool IsMedia(string name) => name is Mute or VolumeUp or VolumeDown or PlayPause or Next or Previous;

    public static bool IsCustom(string? name) =>
        name is { Length: > 7 and <= 71 } && name.StartsWith(CustomPrefix, StringComparison.Ordinal)
        && name[CustomPrefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsKnown(string? name) => name is not null && (Session.Contains(name) || System.Contains(name) || IsCustom(name));

    /// <summary>What the phone shows on the button.</summary>
    public static string Label(string name) => name switch
    {
        Lock => "잠그기",
        ScreenOff => "화면 끄기",
        Mute => "음소거",
        VolumeUp => "소리 크게",
        VolumeDown => "소리 작게",
        PlayPause => "재생/일시정지",
        Next => "다음",
        Previous => "이전",
        Restart => "다시 시작",
        Sleep => "절전",
        _ => name,
    };
}

public static class CommandKinds
{
    public const string Session = "session";
    public const string System = "system";
    public const string Custom = "custom";
}

/// <summary>
/// <c>GET /api/v1/commands</c>: what this PC lets phones ask for, in the order to show. <see cref="Wake"/>: its network
/// adapters' MAC addresses, for Wake-on-LAN from the phone while it sleeps. <see cref="Pending"/>: a restart under way.
/// </summary>
public sealed record CommandList(int Schema, IReadOnlyList<CommandInfo> Commands, CommandWake? Wake = null, CommandPending? Pending = null);

/// <summary><see cref="Kind"/>: <see cref="CommandKinds"/>. <see cref="Confirm"/>: the phone asks to confirm and unlock first.</summary>
public sealed record CommandInfo(string Name, string Label, string Kind, bool Confirm);

/// <summary>MAC addresses (<c>aa:bb:cc:dd:ee:ff</c>) of this PC's wired and wireless adapters.</summary>
public sealed record CommandWake(IReadOnlyList<string> Macs);

public sealed record CommandPending(string Name, DateTimeOffset At);

/// <summary>Sealed (M-62 key) in <c>POST /api/v1/commands</c> and <c>/cancel</c>.</summary>
public sealed record CommandRequest(int Schema, string Command, DateTimeOffset Ts);

/// <summary>
/// Answer. <see cref="State"/>: <c>done</c>, <c>scheduled</c> (a restart at <see cref="At"/>), <c>cancelled</c>.
/// <see cref="Error"/>: <see cref="CommandErrors"/>, or <c>clip_key_needed</c> / <c>no_user_session</c> (<see cref="ClipErrors"/>).
/// </summary>
public sealed record CommandResult(int Schema, bool Ok, string? State = null, string? Error = null, DateTimeOffset? At = null);

public static class CommandStates
{
    public const string Done = "done";
    public const string Scheduled = "scheduled";
    public const string Cancelled = "cancelled";
}

public static class CommandErrors
{
    /// <summary>Not a command this PC has turned on (or not one at all), or asked by a PC (phones only).</summary>
    public const string NotAllowed = "not_allowed";
    /// <summary>Doesn't open with the agreed key, or not a request of this schema.</summary>
    public const string Invalid = "invalid_command";
    /// <summary>This id was carried out already, or the request is too old or from the future.</summary>
    public const string Replayed = "replayed";
    /// <summary>Too many requests in a minute.</summary>
    public const string Busy = "busy";
    /// <summary>Nothing to cancel.</summary>
    public const string NothingPending = "nothing_pending";
    public const string Failed = "failed";
}

public static class CommandMessages
{
    private static readonly JsonSerializerOptions Json = ClipProtocol.ContentJson;

    public static ClipEnvelope Seal(byte[] key, string id, CommandRequest request, byte[]? nonce = null)
    {
        var (n, data) = ClipCrypto.Seal(key, JsonSerializer.SerializeToUtf8Bytes(request, Json), nonce);
        return new ClipEnvelope(MobileProtocol.Schema, id, n, data);
    }

    /// <summary>The request, or null when it isn't ours, doesn't open with <paramref name="key"/>, or names no known command.</summary>
    public static CommandRequest? Open(byte[] key, ClipEnvelope? envelope)
    {
        if (envelope is null || envelope.Schema != MobileProtocol.Schema || !ClipProtocol.IsValidId(envelope.Id)) return null;
        var plain = ClipCrypto.Open(key, envelope.Nonce, envelope.Data);
        if (plain is null) return null;
        try
        {
            return JsonSerializer.Deserialize<CommandRequest>(plain, Json) is { Schema: MobileProtocol.Schema } r && RemoteCommandNames.IsKnown(r.Command)
                ? r
                : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Bridge payloads between the Agent and the Tray. <c>command.run</c> (Agent → Tray): a session or custom command, answered
/// <see cref="TrayCommandDone"/>. <c>commands.custom</c> (Tray → Agent): the custom commands this user registered — only
/// their ids and names; what they run stays with the Tray.
/// </summary>
public sealed record TrayCommand(string Id, string Name, string From);

public sealed record TrayCommandDone(string Id, bool Ok, string? Error = null);

public sealed record TrayCustomCommands(IReadOnlyList<TrayCustomCommand> Commands);

public sealed record TrayCustomCommand(string Id, string Name);
