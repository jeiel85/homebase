using LocalOpsBot.Core.Alerts;

namespace LocalOpsBot.Core.Commands;

public sealed class MuteCommandHandler : ICommandHandler
{
    private readonly IStateStore _stateStore;
    private const string MutedUntilKey = "alert.muted_until";

    public string CommandName => "mute";
    public string Description => "Mute alerts for a duration (e.g., /mute 1h, /mute 30m)";

    public MuteCommandHandler(IStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var durationStr = command.Args.Count > 0 ? command.Args[0] : "1h";
        var duration = ParseDuration(durationStr);

        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7))
            return new CommandResult(false, "Invalid duration. Use e.g., /mute 30m, /mute 2h, /mute 1d (max 7d).");

        var mutedUntil = DateTime.UtcNow.Add(duration);
        await _stateStore.SetAsync(MutedUntilKey, mutedUntil.ToString("O"), ct);

        return new CommandResult(true,
            $"\U0001f515 Alerts muted for {FormatDuration(duration)} (until {mutedUntil.ToLocalTime():HH:mm}).");
    }

    private static TimeSpan ParseDuration(string input)
    {
        input = input.Trim().ToLowerInvariant();
        if (input.EndsWith('h') && int.TryParse(input.TrimEnd('h'), out var hours))
            return TimeSpan.FromHours(hours);
        if (input.EndsWith('m') && int.TryParse(input.TrimEnd('m'), out var mins))
            return TimeSpan.FromMinutes(mins);
        if (input.EndsWith('d') && int.TryParse(input.TrimEnd('d'), out var days))
            return TimeSpan.FromDays(days);
        if (int.TryParse(input, out var num))
            return TimeSpan.FromHours(num);
        return TimeSpan.Zero;
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{(int)d.TotalMinutes}m";
    }
}
