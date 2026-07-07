using LocalOpsBot.Core.Commands;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class CommandRouterTests
{
    [Fact]
    public async Task RouteAsync_forwards_to_matching_handler()
    {
        var handler = new FakeCommandHandler("ping");
        var router = new CommandRouter([handler]);

        var cmd = new BotCommand("ping", [], 1, null, "/ping", DateTimeOffset.UtcNow);
        var result = await router.RouteAsync(cmd, default);

        Assert.True(result.Success);
        Assert.Equal("handled:ping", result.ResponseText);
    }

    [Fact]
    public async Task RouteAsync_returns_unknown_for_unregistered()
    {
        var router = new CommandRouter([]);

        var cmd = new BotCommand("unknown", [], 1, null, "/unknown", DateTimeOffset.UtcNow);
        var result = await router.RouteAsync(cmd, default);

        Assert.False(result.Success);
        Assert.Contains("Unknown command", result.ResponseText);
        Assert.Contains("/unknown", result.ResponseText);
    }

    [Fact]
    public async Task RouteAsync_case_insensitive()
    {
        var handler = new FakeCommandHandler("ping");
        var router = new CommandRouter([handler]);

        var cmd = new BotCommand("PING", [], 1, null, "/PING", DateTimeOffset.UtcNow);
        var result = await router.RouteAsync(cmd, default);

        Assert.True(result.Success);
    }

    private sealed class FakeCommandHandler : ICommandHandler
    {
        public string CommandName { get; }
        public string Description => "test";

        public FakeCommandHandler(string name) => CommandName = name;

        public Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
            => Task.FromResult(new CommandResult(true, $"handled:{command.Name}"));
    }
}
