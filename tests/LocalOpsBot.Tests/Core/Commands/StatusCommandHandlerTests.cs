using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class StatusCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_includes_cpu_ram_network_disk_uptime()
    {
        var handler = new StatusCommandHandler(
            new FakeSystemMetricsCollector(),
            new FakeDiskCollector(),
            new FakeNetworkStatusChecker());

        var cmd = new BotCommand("status", [], 1, null, "/status", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("CPU", result.ResponseText);
        Assert.Contains("RAM", result.ResponseText);
        Assert.Contains("Network", result.ResponseText);
        Assert.Contains("Disk", result.ResponseText);
        Assert.Contains("Uptime", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_shows_unknown_on_failure()
    {
        var metrics = new FakeSystemMetricsCollector
        {
            NextResult = CollectorResult<SystemMetricSnapshot>.Fail("fail", DateTimeOffset.UtcNow)
        };
        var disk = new FakeDiskCollector
        {
            NextResult = CollectorResult<IReadOnlyList<DiskSnapshot>>.Fail("fail", DateTimeOffset.UtcNow)
        };
        var network = new FakeNetworkStatusChecker
        {
            NextResult = CollectorResult<NetworkStatusSnapshot>.Fail("fail", DateTimeOffset.UtcNow)
        };

        var handler = new StatusCommandHandler(metrics, disk, network);
        var cmd = new BotCommand("status", [], 1, null, "/status", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("unknown", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_has_host_name_header()
    {
        var handler = new StatusCommandHandler(
            new FakeSystemMetricsCollector(),
            new FakeDiskCollector(),
            new FakeNetworkStatusChecker());

        var cmd = new BotCommand("status", [], 1, null, "/status", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.Contains("TEST-PC", result.ResponseText);
    }
}
