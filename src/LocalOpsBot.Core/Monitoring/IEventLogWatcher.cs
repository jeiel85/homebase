namespace LocalOpsBot.Core.Monitoring;

public interface IEventLogWatcher
{
    Task<IReadOnlyList<WindowsEventLogItem>> PollAsync(EventLogOptions options, CancellationToken ct);
}

public sealed record EventLogOptions(
    bool Enabled = true,
    IReadOnlyList<string>? Logs = null,
    IReadOnlyList<string>? Levels = null,
    IReadOnlyList<string>? ProviderIncludes = null,
    IReadOnlyList<string>? ProviderExcludes = null,
    int MessageMaxChars = 500)
{
    public IReadOnlyList<string> Logs { get; init; } = Logs ?? new[] { "Application", "System" };
    public IReadOnlyList<string> Levels { get; init; } = Levels ?? new[] { "Critical", "Error" };
}
