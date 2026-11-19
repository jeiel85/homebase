using System.Security.Cryptography;
using System.Text.Json;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>
/// Remote commands (DECISIONS M-69): only what this PC turned on, only from phones, sealed with the agreed key, each request
/// once and a few a minute; session ones go to the Tray, restart and sleep are the Agent's.
/// </summary>
public class RemoteCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new("6f1c0d2e9b8a4c7d");
    private readonly InMemoryClipKeys _keys = new();
    private readonly PairingService _pairing;
    private readonly FakePower _power = new();
    private readonly List<(PushTarget Target, byte[] Plaintext)> _pushed = new();
    private readonly List<TrayCommand> _tray = new();
    private readonly MobileOptions _options = new();
    private bool? _trayAnswer = true;
    private RemoteCommandService _commands;

    public RemoteCommandTests()
    {
        _pairing = new PairingService(_store, _time);
        _commands = NewService();
    }

    private RemoteCommandService NewService()
    {
        var commands = new RemoteCommandService(_store, _keys, () => _options, _time, NullLogger.Instance, _dir, (t, p, _) =>
        {
            lock (_pushed) _pushed.Add((t, p));
            return Task.FromResult((true, (string?)null));
        }, power: _power);
        commands.Tray = (c, _) =>
        {
            lock (_tray) _tray.Add(c);
            return Task.FromResult(_trayAnswer);
        };
        return commands;
    }

    private (string Token, byte[] Key, MobileDeviceInfo Device) Phone(string name = "Galaxy S24", string kind = DeviceKind.Phone)
    {
        var (code, _) = _pairing.Begin();
        var token = _pairing.TryPair(code, name, null, kind).Token!;
        var device = _store.Authenticate(token)!;
        _store.SetPushSubscription(device.DeviceId, new PushSubscription("https://ntfy.sh/up" + device.DeviceId, "k", "a", _time.GetUtcNow()));
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Set(ClipTargetKeys.Device(device.DeviceId), key);
        return (token, key, device);
    }

    private ClipEnvelope Request(byte[] key, string command, DateTimeOffset? ts = null, string? id = null) =>
        CommandMessages.Seal(key, id ?? ClipProtocol.NewId(), new CommandRequest(MobileProtocol.Schema, command, ts ?? _time.GetUtcNow()));

    [Fact]
    public void By_default_lock_screen_and_media_are_offered_and_restart_and_sleep_are_not()
    {
        var (_, _, phone) = Phone();
        var list = _commands.List(phone);

        Assert.Equal(new[] { "lock", "screen_off", "mute", "volume_up", "volume_down", "media_play_pause", "media_next", "media_previous" },
            list.Commands.Select(c => c.Name));
        Assert.All(list.Commands, c => Assert.Equal((CommandKinds.Session, false), (c.Kind, c.Confirm)));

        _options.Commands.Restart = true;
        _options.Commands.Lock = false;
        _commands.Custom = () => [new TrayCustomCommand("backup01", "백업")];
        list = _commands.List(phone);
        Assert.DoesNotContain(list.Commands, c => c.Name == RemoteCommandNames.Lock);
        Assert.Contains(new CommandInfo("restart", "다시 시작", CommandKinds.System, true), list.Commands);
        Assert.Contains(new CommandInfo("custom:backup01", "백업", CommandKinds.Custom, true), list.Commands);
    }

    [Fact]
    public async Task A_session_command_goes_to_the_tray_once()
    {
        var (_, key, phone) = Phone();
        var envelope = Request(key, RemoteCommandNames.Lock);

        var (status, result) = await _commands.RunAsync(phone, envelope, default);
        Assert.Equal((200, true, CommandStates.Done), (status, result.Ok, result.State));
        Assert.Equal((RemoteCommandNames.Lock, "Galaxy S24"), (Assert.Single(_tray).Name, _tray[0].From));

        // The same request again (a replay, through the Cloudflare edge or anyone): refused, not run.
        (status, result) = await _commands.RunAsync(phone, envelope, default);
        Assert.Equal((409, CommandErrors.Replayed), (status, result.Error));
        Assert.Single(_tray);
    }

    [Fact]
    public async Task Old_unknown_unsealed_or_turned_off_requests_are_refused()
    {
        var (_, key, phone) = Phone();

        Assert.Equal((409, CommandErrors.Replayed), Outcome(await _commands.RunAsync(phone, Request(key, "lock", _time.GetUtcNow().AddMinutes(-5)), default)));
        Assert.Equal((400, CommandErrors.Invalid), Outcome(await _commands.RunAsync(phone, Request(RandomNumberGenerator.GetBytes(32), "lock"), default)));
        Assert.Equal((400, CommandErrors.Invalid), Outcome(await _commands.RunAsync(phone, Request(key, "shell"), default)));
        Assert.Equal((403, CommandErrors.NotAllowed), Outcome(await _commands.RunAsync(phone, Request(key, "restart"), default)));
        Assert.Equal((403, CommandErrors.NotAllowed), Outcome(await _commands.RunAsync(phone, Request(key, "custom:nothere1"), default)));
        Assert.Empty(_tray);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task Only_phones_may_ask()
    {
        var (_, key, pc) = Phone("WORK-PC", DeviceKind.Pc);
        Assert.Empty(_commands.List(pc).Commands);
        Assert.Equal((403, CommandErrors.NotAllowed), Outcome(await _commands.RunAsync(pc, Request(key, "lock"), default)));
    }

    [Fact]
    public async Task Without_a_signed_in_user_session_commands_say_so_and_a_phone_may_ask_ten_a_minute()
    {
        var (_, key, phone) = Phone();
        _trayAnswer = null;
        Assert.Equal((409, ClipErrors.NoUserSession), Outcome(await _commands.RunAsync(phone, Request(key, "mute"), default)));

        _trayAnswer = true;
        for (var i = 1; i < RemoteCommandProtocol.PerMinute; i++)
            Assert.Equal(200, (await _commands.RunAsync(phone, Request(key, "volume_up"), default)).Status);
        Assert.Equal((429, CommandErrors.Busy), Outcome(await _commands.RunAsync(phone, Request(key, "volume_up"), default)));
        _time.Advance(TimeSpan.FromMinutes(1.1));
        Assert.Equal(200, (await _commands.RunAsync(phone, Request(key, "volume_up"), default)).Status);
    }

    [Fact]
    public async Task A_restart_counts_down_and_can_be_called_off()
    {
        var (_, key, phone) = Phone();
        _options.Commands.Restart = true;
        var notices = new List<DateTimeOffset?>();
        _commands.Notice = (_, at, _) => notices.Add(at);

        var (status, result) = await _commands.RunAsync(phone, Request(key, "restart"), default);

        Assert.Equal((200, CommandStates.Scheduled, _time.GetUtcNow().AddSeconds(60)), (status, result.State, result.At));
        Assert.Equal("restart 60", Assert.Single(_power.Calls));
        Assert.Equal(new CommandPending("restart", _time.GetUtcNow().AddSeconds(60)), _commands.List(phone).Pending);
        Assert.True(File.Exists(Path.Combine(_dir, "restart.json")));

        (status, result) = await _commands.CancelAsync(phone, Request(key, "restart"));
        Assert.Equal((200, CommandStates.Cancelled), (status, result.State));
        Assert.Equal(new[] { "restart 60", "abort" }, _power.Calls);
        Assert.Null(_commands.List(phone).Pending);
        Assert.False(File.Exists(Path.Combine(_dir, "restart.json")));
        Assert.Equal(2, notices.Count);
        Assert.Null(notices[1]);
        Assert.Equal((409, CommandErrors.NothingPending), Outcome(await _commands.CancelAsync(phone, Request(key, "restart"))));
    }

    [Fact]
    public async Task Back_after_a_restart_the_phone_that_asked_is_told()
    {
        var (_, _, phone) = Phone();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "restart.json"), JsonSerializer.Serialize(
            new RemoteCommandService.PendingRestart(phone.DeviceId, phone.Name, DateTimeOffset.UnixEpoch.AddSeconds(60), DateTimeOffset.UnixEpoch)));
        _commands = NewService();

        await _commands.StartAsync(default);
        await (_commands.ExecuteTask ?? Task.CompletedTask);

        var (target, plaintext) = Assert.Single(_pushed);
        Assert.Equal(phone.DeviceId, target.DeviceId);
        Assert.Equal("다시 시작을 마쳤습니다", JsonSerializer.Deserialize<PushMessage>(plaintext, MobileProtocol.Json)!.Title);
        Assert.False(File.Exists(Path.Combine(_dir, "restart.json")));
    }

    [Fact]
    public async Task The_api_lists_and_runs_commands()
    {
        var (token, key, _) = Phone();
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var api = new MobileApiServer(hub, _pairing, _store, new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options, NullLogger<MobileApiServer>.Instance, commands: _commands);
        HttpRequestHead Req(string method, string path) =>
            new(method, path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }, null);

        var (status, json) = await api.HandleRestAsync(Req("GET", RemoteCommandProtocol.Path), null, default);
        Assert.Equal(200, status);
        Assert.Contains(JsonSerializer.Deserialize<CommandList>(json, MobileProtocol.Json)!.Commands, c => c.Name == "lock");

        (status, json) = await api.HandleRestAsync(Req("POST", RemoteCommandProtocol.Path),
            JsonSerializer.SerializeToUtf8Bytes(Request(key, "media_play_pause"), MobileProtocol.Json), default);
        Assert.Equal((200, true), (status, JsonSerializer.Deserialize<CommandResult>(json, MobileProtocol.Json)!.Ok));
        (status, json) = await api.HandleRestAsync(Req("POST", RemoteCommandProtocol.CancelPath),
            JsonSerializer.SerializeToUtf8Bytes(Request(key, "restart"), MobileProtocol.Json), default);
        Assert.Equal((409, CommandErrors.NothingPending), (status, JsonSerializer.Deserialize<CommandResult>(json, MobileProtocol.Json)!.Error));
    }

    private static (int, string?) Outcome((int Status, CommandResult Result) r) => (r.Status, r.Result.Error);

    private sealed class FakePower : ISystemPower
    {
        public List<string> Calls { get; } = new();
        public bool ScheduleRestart(int seconds, string message) { Calls.Add($"restart {seconds}"); return true; }
        public bool AbortRestart() { Calls.Add("abort"); return true; }
        public bool Sleep() { Calls.Add("sleep"); return true; }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
