using LocalOpsBot.Core.Commands;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class PingCommandHandlerTests
{
    private readonly PingCommandHandler _handler = new();

    [Fact]
    public void CommandName_is_ping()
    {
        Assert.Equal("ping", _handler.CommandName);
    }

    [Fact]
    public void Description_is_not_empty()
    {
        Assert.NotEmpty(_handler.Description);
    }

    [Fact]
    public async Task HandleAsync_returns_pong()
    {
        var cmd = new BotCommand("ping", [], 1, null, "/ping", DateTimeOffset.UtcNow);
        var result = await _handler.HandleAsync(cmd, default);

        Assert.True(result.Success);
        Assert.StartsWith("pong", result.ResponseText);
        Assert.True(result.SendResponse);
    }

    [Fact]
    public async Task HandleAsync_includes_machine_name()
    {
        var cmd = new BotCommand("ping", [], 1, null, "/ping", DateTimeOffset.UtcNow);
        var result = await _handler.HandleAsync(cmd, default);

        Assert.Contains(Environment.MachineName, result.ResponseText);
    }
}
