using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class UptimeCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_includes_uptime()
    {
        var metrics = new FakeSystemMetricsCollector();
        var handler = new UptimeCommandHandler(metrics);

        var cmd = new BotCommand("uptime", [], 1, null, "/uptime", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("3d", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_includes_hostname()
    {
        var metrics = new FakeSystemMetricsCollector();
        var handler = new UptimeCommandHandler(metrics);

        var cmd = new BotCommand("uptime", [], 1, null, "/uptime", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.Contains("TEST-PC", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_returns_unavailable_on_failure()
    {
        var metrics = new FakeSystemMetricsCollector
        {
            NextResult = CollectorResult<SystemMetricSnapshot>.Fail("fail", DateTimeOffset.UtcNow)
        };
        var handler = new UptimeCommandHandler(metrics);

        var cmd = new BotCommand("uptime", [], 1, null, "/uptime", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.False(result.Success);
        Assert.Contains("unavailable", result.ResponseText);
    }
}
