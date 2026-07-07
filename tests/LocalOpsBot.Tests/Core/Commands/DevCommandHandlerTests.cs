using LocalOpsBot.Core.Commands;
using LocalOpsBot.Core.Monitoring;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class DevCommandHandlerTests
{
    private sealed class FakeHttpEndpointMonitor : IHttpEndpointMonitor
    {
        public required HttpEndpointResult Result { get; set; }

        public Task<HttpEndpointResult> CheckAsync(HttpEndpointConfig config, CancellationToken ct)
            => Task.FromResult(Result);
    }

    [Fact]
    public async Task HandleAsync_shows_endpoints()
    {
        var monitor = new FakeHttpEndpointMonitor
        {
            Result = new HttpEndpointResult("Test App", "http://localhost:3000", true, 200, 5, null)
        };
        var configs = new[] { new HttpEndpointConfig("Test App", "http://localhost:3000") };
        var handler = new DevCommandHandler(monitor, configs);

        var cmd = new BotCommand("dev", [], 1, null, "/dev", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("Test App", result.ResponseText);
        Assert.Contains("OK", result.ResponseText);
        Assert.Contains("5ms", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_shows_down_endpoint()
    {
        var monitor = new FakeHttpEndpointMonitor
        {
            Result = new HttpEndpointResult("Down App", "http://localhost:9999", false, null, null, "Connection refused")
        };
        var configs = new[] { new HttpEndpointConfig("Down App", "http://localhost:9999") };
        var handler = new DevCommandHandler(monitor, configs);

        var cmd = new BotCommand("dev", [], 1, null, "/dev", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Contains("Down App", result.ResponseText);
        Assert.Contains("Connection refused", result.ResponseText);
    }

    [Fact]
    public async Task HandleAsync_returns_message_when_no_endpoints()
    {
        var monitor = new FakeHttpEndpointMonitor
        {
            Result = new HttpEndpointResult("", "", false, null, null, "")
        };
        var handler = new DevCommandHandler(monitor, Array.Empty<HttpEndpointConfig>());

        var cmd = new BotCommand("dev", [], 1, null, "/dev", DateTimeOffset.UtcNow);
        var result = await handler.HandleAsync(cmd, default);

        Assert.False(result.Success);
        Assert.Contains("No HTTP endpoints configured", result.ResponseText);
    }
}
