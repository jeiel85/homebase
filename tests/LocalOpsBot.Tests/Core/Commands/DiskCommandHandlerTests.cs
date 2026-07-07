using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class DiskCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_includes_drive_info()
    {
        var disk = new FakeDiskCollector();
        var handler = new DiskCommandHandler(disk);

        var cmd = new BotCommand("disk", [], 1, null, "/disk", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("C:", result.ResponseText);
        Assert.Contains("GB", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_returns_unavailable_on_failure()
    {
        var disk = new FakeDiskCollector
        {
            NextResult = CollectorResult<IReadOnlyList<DiskSnapshot>>.Fail("fail", DateTimeOffset.UtcNow)
        };
        var handler = new DiskCommandHandler(disk);

        var cmd = new BotCommand("disk", [], 1, null, "/disk", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.False(result.Success);
        Assert.Contains("unavailable", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_includes_free_and_used()
    {
        var disk = new FakeDiskCollector();
        var handler = new DiskCommandHandler(disk);

        var cmd = new BotCommand("disk", [], 1, null, "/disk", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.Contains("Free", result.ResponseText);
    }
}
