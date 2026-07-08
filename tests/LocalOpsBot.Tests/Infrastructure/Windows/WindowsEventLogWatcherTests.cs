using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Windows;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure.Windows;

// Windows-only: these exercise the real Application event log (readable without elevation).
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogWatcherTests
{
    private static EventLogOptions ApplicationLogOptions() => new()
    {
        Logs = new[] { "Application" },
        Levels = new[] { "Critical", "Error", "Warning", "Information" },
        MessageMaxChars = 200
    };

    [Fact]
    public async Task First_poll_establishes_baseline_and_emits_nothing()
    {
        var watcher = new WindowsEventLogWatcher();

        var result = await watcher.PollAsync(ApplicationLogOptions(), CancellationToken.None);

        // The first poll only records a resume point; it must never replay existing history
        // (otherwise a service restart would alert-storm on old errors).
        Assert.Empty(result);
    }

    [Fact]
    public async Task Resume_after_baseline_does_not_replay_the_whole_log()
    {
        var watcher = new WindowsEventLogWatcher();
        var options = ApplicationLogOptions();

        await watcher.PollAsync(options, CancellationToken.None);          // baseline
        var second = await watcher.PollAsync(options, CancellationToken.None); // resume from bookmark

        // Resuming from the bookmark reads only events written since the baseline — at most a
        // few in the moment between polls, never the (typically thousands-deep) Application log.
        // A large count here would mean the bookmark resume regressed to a full re-scan.
        Assert.True(second.Count < 200, $"expected a bounded resume, got {second.Count} events");
    }
}
