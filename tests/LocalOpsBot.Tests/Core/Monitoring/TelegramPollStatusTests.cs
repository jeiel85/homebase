using LocalOpsBot.Core.Monitoring;
using Xunit;

namespace LocalOpsBot.Tests.Core.Monitoring;

public sealed class TelegramPollStatusTests
{
    [Fact]
    public void New_status_has_no_poll_and_zero_failures()
    {
        var status = new TelegramPollStatus();
        Assert.Null(status.LastSuccessfulPollUtc);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFailure_increments_consecutive_count()
    {
        var status = new TelegramPollStatus();
        status.RecordFailure();
        status.RecordFailure();
        status.RecordFailure();
        Assert.Equal(3, status.ConsecutiveFailures);
    }

    [Fact]
    public void RecordSuccess_sets_last_poll_and_resets_failures()
    {
        var status = new TelegramPollStatus();
        status.RecordFailure();
        status.RecordFailure();

        var before = DateTimeOffset.UtcNow;
        status.RecordSuccess();
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.NotNull(status.LastSuccessfulPollUtc);
        Assert.InRange(status.LastSuccessfulPollUtc!.Value, before, after);
    }

    [Fact]
    public void Failures_after_a_success_count_from_zero_again()
    {
        var status = new TelegramPollStatus();
        status.RecordFailure();
        status.RecordSuccess();
        status.RecordFailure();
        Assert.Equal(1, status.ConsecutiveFailures);
    }
}
