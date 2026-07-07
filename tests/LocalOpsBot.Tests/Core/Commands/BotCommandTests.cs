using LocalOpsBot.Core.Commands;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class BotCommandTests
{
    [Fact]
    public void Parse_ping_command()
    {
        var cmd = new BotCommand(
            Name: "ping",
            Args: Array.Empty<string>(),
            ChatId: 123,
            UserId: 456,
            RawText: "/ping",
            ReceivedAt: DateTimeOffset.UtcNow);

        Assert.Equal("ping", cmd.Name);
        Assert.Empty(cmd.Args);
    }
}
