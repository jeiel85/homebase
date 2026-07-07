using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class PortsCommandHandlerTests
{
    private sealed class FakeTcpPortMonitor : ITcpPortMonitor
    {
        public required TcpPortResult Result { get; set; }

        public Task<TcpPortResult> CheckAsync(TcpPortConfig config, CancellationToken ct)
            => Task.FromResult(Result);
    }

    [Fact]
    public async Task HandleAsync_shows_open_ports()
    {
        var monitor = new FakeTcpPortMonitor
        {
            Result = new TcpPortResult("PostgreSQL", "127.0.0.1", 5432, true, 2, null)
        };
        var configs = new[] { new TcpPortConfig("PostgreSQL", "127.0.0.1", 5432) };
        var handler = new PortsCommandHandler(monitor, configs);

        var cmd = new BotCommand("ports", [], 1, null, "/ports", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("PostgreSQL", result.ResponseText);
        Assert.Contains("Open", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_shows_closed_ports()
    {
        var monitor = new FakeTcpPortMonitor
        {
            Result = new TcpPortResult("Closed Port", "127.0.0.1", 9999, false, null, "Connection refused")
        };
        var configs = new[] { new TcpPortConfig("Closed Port", "127.0.0.1", 9999) };
        var handler = new PortsCommandHandler(monitor, configs);

        var cmd = new BotCommand("ports", [], 1, null, "/ports", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("Closed Port", result.ResponseText);
        Assert.Contains("Connection refused", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_returns_message_when_no_ports()
    {
        var monitor = new FakeTcpPortMonitor
        {
            Result = new TcpPortResult("", "", 0, false, null, "")
        };
        var handler = new PortsCommandHandler(monitor, Array.Empty<TcpPortConfig>());

        var cmd = new BotCommand("ports", [], 1, null, "/ports", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.False(result.Success);
        Assert.Contains("No TCP ports configured", result.ResponseText);
    }
}
