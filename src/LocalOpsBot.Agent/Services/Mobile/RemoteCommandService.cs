using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>Restart, cancel it, sleep — what the Agent (LocalSystem) does with this PC's power.</summary>
public interface ISystemPower
{
    bool ScheduleRestart(int seconds, string message);
    bool AbortRestart();
    bool Sleep();
}

/// <summary>Windows' own: <c>shutdown.exe /r /t</c> (its warning included), <c>shutdown.exe /a</c>, <c>SetSuspendState</c>.</summary>
public sealed class WindowsSystemPower : ISystemPower
{
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public bool ScheduleRestart(int seconds, string message) =>
        Run("/r", "/t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "/d", "p:0:0", "/c", message.Length > 500 ? message[..500] : message);

    public bool AbortRestart() => Run("/a");

    public bool Sleep() => SetSuspendState(false, false, false);

    private static bool Run(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start);
        if (p is null) return false;
        p.WaitForExit(10_000);
        return p.HasExited && p.ExitCode == 0;
    }
}

/// <summary>
/// Commands phones ask this PC to carry out (DECISIONS M-69, <c>docs/REMOTE_COMMANDS.md</c>). Only what is turned on here
/// (<c>mobile.commands</c>; custom ones the signed-in user registered in the Tray), only from phones, sealed with the key the
/// phone agreed with this PC, each request once, a few a minute. Session commands go to the Tray (the user's session); restart
/// and sleep are the Agent's. Every one is kept in the notification center.
/// </summary>
public sealed class RemoteCommandService : BackgroundService
{
    private const string HistoryApp = RemoteCommandProtocol.HistoryApp;
    private static readonly TimeSpan TrayWait = TimeSpan.FromSeconds(10);
    /// <summary>A restart that hasn't happened this long after its time was called off at the PC.</summary>
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(45);

    private readonly IMobileDeviceStore _devices;
    private readonly IClipKeyStore _keys;
    private readonly Func<MobileOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ClipShareService.PushSender? _push;
    private readonly INotificationHistoryStore? _history;
    private readonly ISystemPower _power;
    private readonly string _pendingPath;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private PendingRestart? _pending;
    private CancellationTokenSource? _watch;

    /// <summary>A restart under way, kept on disk so the PC can tell the phone once it is back.</summary>
    internal sealed record PendingRestart(string DeviceId, string DeviceName, DateTimeOffset At, DateTimeOffset RequestedAt);

    public RemoteCommandService(IMobileDeviceStore devices, IClipKeyStore keys, Func<MobileOptions> options, TimeProvider time, ILogger logger,
        string stateDirectory, ClipShareService.PushSender? push = null, INotificationHistoryStore? history = null, ISystemPower? power = null)
    {
        _devices = devices;
        _keys = keys;
        _options = options;
        _time = time;
        _logger = logger;
        _push = push;
        _history = history;
        _power = power ?? new WindowsSystemPower();
        Directory.CreateDirectory(stateDirectory);
        _pendingPath = Path.Combine(stateDirectory, "restart.json");
    }

    /// <summary>Runs a session or custom command in the signed-in user's Tray (set by the bridge); null answer = no Tray.</summary>
    public Func<TrayCommand, CancellationToken, Task<bool?>>? Tray { get; set; }

    /// <summary>The custom commands the connected Trays registered (set by the bridge).</summary>
    public Func<IReadOnlyList<TrayCustomCommand>>? Custom { get; set; }

    /// <summary>Tells the Trays a restart was scheduled or called off, for the countdown toast (set by the bridge).</summary>
    public Action<string, DateTimeOffset?, string>? Notice { get; set; }

    // ── API ────────────────────────────────────────────────────────────────

    /// <summary><c>GET /api/v1/commands</c>: what is turned on, in the order to show, and this PC's MACs for waking it.</summary>
    public CommandList List(MobileDeviceInfo device)
    {
        if (device.Kind == DeviceKind.Pc) return new CommandList(MobileProtocol.Schema, Array.Empty<CommandInfo>());
        var commands = new List<CommandInfo>();
        foreach (var name in RemoteCommandNames.Session.Where(Allowed))
            commands.Add(new CommandInfo(name, RemoteCommandNames.Label(name), CommandKinds.Session, Confirm: false));
        foreach (var name in RemoteCommandNames.System.Where(Allowed))
            commands.Add(new CommandInfo(name, RemoteCommandNames.Label(name), CommandKinds.System, Confirm: true));
        foreach (var custom in CustomCommands())
            commands.Add(new CommandInfo(RemoteCommandNames.CustomPrefix + custom.Id, custom.Name, CommandKinds.Custom, Confirm: true));
        PendingRestart? pending;
        lock (_gate) pending = _pending;
        return new CommandList(MobileProtocol.Schema, commands,
            _options().Commands.Wake && Macs() is { Count: > 0 } macs ? new CommandWake(macs) : null,
            pending is null ? null : new CommandPending(RemoteCommandNames.Restart, pending.At));
    }

    /// <summary><c>POST /api/v1/commands</c>: the HTTP status and the answer.</summary>
    public async Task<(int Status, CommandResult Result)> RunAsync(MobileDeviceInfo device, ClipEnvelope? envelope, CancellationToken ct)
    {
        if (Admit(device, envelope) is { } refused) return refused;
        var request = CommandMessages.Open(_keys.Get(ClipTargetKeys.Device(device.DeviceId))!, envelope)!;
        var name = request.Command;
        (int, CommandResult) result;
        if (!Allowed(name))
            result = (403, Error(CommandErrors.NotAllowed));
        else if (name == RemoteCommandNames.Restart)
            result = Restart(device);
        else if (name == RemoteCommandNames.Sleep)
            result = Sleep();
        else
            result = await InTrayAsync(envelope!.Id, name, device, ct);
        await RecordAsync(device, name, result.Item2);
        _logger.LogInformation("Remote command {Command} from {Name}: {Outcome}.", name, device.Name,
            result.Item2.Ok ? result.Item2.State : result.Item2.Error);
        return result;
    }

    /// <summary><c>POST /api/v1/commands/cancel</c>: calls off a restart under way.</summary>
    public async Task<(int Status, CommandResult Result)> CancelAsync(MobileDeviceInfo device, ClipEnvelope? envelope)
    {
        if (Admit(device, envelope) is { } refused) return refused;
        var result = Cancel(device.Name);
        await RecordAsync(device, "restart-cancel", result.Item2);
        return result;
    }

    /// <summary>The Tray's [Cancel] (its toast or menu): calls off a restart under way.</summary>
    public bool CancelFromTray() => Cancel("이 PC").Item2.Ok;

    /// <summary>
    /// Phones only; the agreed key opens it; a known command; not seen before and not too old or new; not too many. The
    /// request is remembered here, so a repeat is refused whatever it asked.
    /// </summary>
    private (int, CommandResult)? Admit(MobileDeviceInfo device, ClipEnvelope? envelope)
    {
        if (device.Kind == DeviceKind.Pc) return (403, Error(CommandErrors.NotAllowed));
        if (_keys.Get(ClipTargetKeys.Device(device.DeviceId)) is not { } key) return (409, Error(ClipErrors.KeyNeeded));
        if (CommandMessages.Open(key, envelope) is not { } request) return (400, Error(CommandErrors.Invalid));
        var now = _time.GetUtcNow();
        if ((now - request.Ts).Duration() > RemoteCommandProtocol.MaxSkew) return (409, Error(CommandErrors.Replayed));
        foreach (var (k, at) in _seen)
            if (now - at > RemoteCommandProtocol.RememberFor) _seen.TryRemove(k, out _);
        if (!_seen.TryAdd(device.DeviceId + ":" + envelope!.Id, now)) return (409, Error(CommandErrors.Replayed));
        var recent = _recent.GetOrAdd(device.DeviceId, _ => new Queue<DateTimeOffset>());
        lock (recent)
        {
            while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1)) recent.Dequeue();
            if (recent.Count >= RemoteCommandProtocol.PerMinute) return (429, Error(CommandErrors.Busy));
            recent.Enqueue(now);
        }
        return null;
    }

    private bool Allowed(string name)
    {
        var c = _options().Commands;
        return name switch
        {
            RemoteCommandNames.Lock => c.Lock,
            RemoteCommandNames.ScreenOff => c.ScreenOff,
            RemoteCommandNames.Restart => c.Restart,
            RemoteCommandNames.Sleep => c.Sleep,
            _ when RemoteCommandNames.IsMedia(name) => c.Media,
            _ when RemoteCommandNames.IsCustom(name) => CustomCommands().Any(x => RemoteCommandNames.CustomPrefix + x.Id == name),
            _ => false,
        };
    }

    private IReadOnlyList<TrayCustomCommand> CustomCommands() =>
        (Custom?.Invoke() ?? Array.Empty<TrayCustomCommand>()).Where(x => RemoteCommandNames.IsCustom(RemoteCommandNames.CustomPrefix + x.Id)).ToList();

    private async Task<(int, CommandResult)> InTrayAsync(string id, string name, MobileDeviceInfo device, CancellationToken ct)
    {
        if (Tray is not { } tray) return (409, Error(ClipErrors.NoUserSession));
        bool? ran;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TrayWait);
            ran = await tray(new TrayCommand(id, name, device.Name), limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ran = false;
        }
        return ran switch
        {
            null => (409, Error(ClipErrors.NoUserSession)),
            true => (200, new CommandResult(MobileProtocol.Schema, true, CommandStates.Done)),
            false => (500, Error(CommandErrors.Failed)),
        };
    }

    private (int, CommandResult) Restart(MobileDeviceInfo device)
    {
        lock (_gate)
        {
            if (_pending is { } already) return (200, new CommandResult(MobileProtocol.Schema, true, CommandStates.Scheduled, At: already.At));
            var at = _time.GetUtcNow().AddSeconds(RemoteCommandProtocol.RestartDelaySeconds);
            if (!_power.ScheduleRestart(RemoteCommandProtocol.RestartDelaySeconds, $"{device.Name}에서 다시 시작을 요청했습니다. Homebase 트레이나 알림에서 취소할 수 있습니다."))
                return (500, Error(CommandErrors.Failed));
            _pending = new PendingRestart(device.DeviceId, device.Name, at, _time.GetUtcNow());
            SavePending();
            Watch(_pending);
            Notice?.Invoke(RemoteCommandNames.Restart, at, device.Name);
            return (200, new CommandResult(MobileProtocol.Schema, true, CommandStates.Scheduled, At: at));
        }
    }

    private (int, CommandResult) Sleep()
    {
        // Answered first: the PC is unreachable once it sleeps.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _time);
            if (!_power.Sleep()) _logger.LogWarning("Sleep was refused (error {Error}).", Marshal.GetLastWin32Error());
        });
        return (200, new CommandResult(MobileProtocol.Schema, true, CommandStates.Done));
    }

    private (int, CommandResult) Cancel(string by)
    {
        PendingRestart? pending;
        lock (_gate)
        {
            pending = _pending;
            if (pending is null) return (409, Error(CommandErrors.NothingPending));
            if (!_power.AbortRestart()) _logger.LogWarning("shutdown /a failed; the restart may already be under way.");
            _pending = null;
            _watch?.Cancel();
            DeletePending();
        }
        _logger.LogInformation("Restart asked by {Device} called off by {By}.", pending.DeviceName, by);
        Notice?.Invoke(RemoteCommandNames.Restart, null, by);
        _ = TellAsync(pending.DeviceId, "다시 시작을 취소했습니다", $"{by}에서 취소했습니다.");
        return (200, new CommandResult(MobileProtocol.Schema, true, CommandStates.Cancelled));
    }

    /// <summary>
    /// Still running well after the restart's time: someone called it off at the PC (Windows' own <c>shutdown /a</c>, or the
    /// toast's [Cancel]), so the phone that asked is told.
    /// </summary>
    private void Watch(PendingRestart pending)
    {
        _watch?.Cancel();
        var cts = _watch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var wait = pending.At - _time.GetUtcNow() + RestartGrace;
            try { await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, _time, cts.Token); }
            catch (OperationCanceledException) { return; }
            lock (_gate)
            {
                if (_pending != pending) return;
                _pending = null;
                DeletePending();
            }
            _logger.LogInformation("The restart asked by {Device} did not happen: called off at the PC.", pending.DeviceName);
            Notice?.Invoke(RemoteCommandNames.Restart, null, "이 PC");
            await TellAsync(pending.DeviceId, "다시 시작이 취소됐습니다", "PC에서 취소했습니다.");
        });
    }

    // ── Back after a restart ────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = LoadPending();
        if (pending is null) return;
        var bootedAt = _time.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (bootedAt > pending.RequestedAt)
        {
            // It restarted: the phone that asked hears it (all phones get the usual "PC started" too, unless muted).
            DeletePending();
            await TellAsync(pending.DeviceId, "다시 시작을 마쳤습니다", $"{Environment.MachineName}이(가) 다시 켜졌습니다.");
            return;
        }
        // Only the Agent restarted during the countdown: keep watching.
        lock (_gate) _pending = pending;
        Watch(pending);
    }

    private async Task TellAsync(string deviceId, string title, string body)
    {
        if (_push is null) return;
        var target = _devices.PushTargets().FirstOrDefault(t => t.DeviceId == deviceId);
        if (target is null) return;
        var message = new PushMessage(MobileProtocol.Schema, Guid.NewGuid().ToString("N"), _devices.PcId, Environment.MachineName, "commands",
            PushPriority.Info, title, body, HistoryApp, 1, _time.GetUtcNow());
        try
        {
            var (ok, error) = await _push(target, PushPayload.Serialize(message), CancellationToken.None);
            if (!ok) _logger.LogWarning("Telling {Device} about the restart failed: {Error}", target.DeviceName, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telling {Device} about the restart failed", target.DeviceName);
        }
    }

    private void SavePending()
    {
        try { File.WriteAllText(_pendingPath, JsonSerializer.Serialize(_pending)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning("Could not keep the restart: {Message}", ex.Message); }
    }

    private void DeletePending()
    {
        try { File.Delete(_pendingPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private PendingRestart? LoadPending()
    {
        try { return File.Exists(_pendingPath) ? JsonSerializer.Deserialize<PendingRestart>(File.ReadAllText(_pendingPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // ── Kept in the notification center ─────────────────────────────────────

    private async Task RecordAsync(MobileDeviceInfo device, string name, CommandResult result)
    {
        if (_history is null) return;
        var label = name == "restart-cancel" ? "다시 시작 취소" : Label(name);
        var outcome = result.Ok
            ? result.State switch
            {
                CommandStates.Scheduled => $"{RemoteCommandProtocol.RestartDelaySeconds}초 뒤 다시 시작",
                CommandStates.Cancelled => "취소함",
                _ => "실행함",
            }
            : "거절: " + result.Error;
        try
        {
            await _history.AddAsync(new DeviceNotificationRecord($"command-{Guid.NewGuid():N}", device.DeviceId, device.Name, device.Kind,
                HistoryApp, label, outcome, _time.GetUtcNow(), _time.GetUtcNow()), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep a remote command in the notification center");
        }
    }

    private string Label(string name) => RemoteCommandNames.IsCustom(name)
        ? CustomCommands().FirstOrDefault(c => RemoteCommandNames.CustomPrefix + c.Id == name)?.Name ?? name
        : RemoteCommandNames.Label(name);

    private static CommandResult Error(string error) => new(MobileProtocol.Schema, false, Error: error);

    /// <summary>MAC addresses of this PC's physical wired and wireless adapters, for Wake-on-LAN.</summary>
    internal static IReadOnlyList<string> Macs()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.Wireless80211)
                .Where(n => !IsVirtual(n.Description) && !IsVirtual(n.Name))
                .Select(n => n.GetPhysicalAddress().GetAddressBytes())
                .Where(b => b.Length == 6 && b.Any(x => x != 0))
                .Select(b => string.Join(":", b.Select(x => x.ToString("x2"))))
                .Distinct()
                .Take(4)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsVirtual(string text) =>
        new[] { "virtual", "hyper-v", "vmware", "vpn", "tailscale", "wintun", "tap-", "loopback", "bluetooth", "miniport", "wsl", "vethernet" }
            .Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));
}
