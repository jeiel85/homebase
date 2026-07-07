using LocalOpsBot.Core.Commands;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class BotCommandParserTests
{
    [Fact]
    public void Parse_ping()
    {
        var cmd = BotCommandParser.Parse("/ping", 1, 100, DateTimeOffset.UtcNow);
        Assert.NotNull(cmd);
        Assert.Equal("ping", cmd.Name);
        Assert.Empty(cmd.Args);
        Assert.Equal(1, cmd.ChatId);
        Assert.Equal(100, cmd.UserId);
    }

    [Fact]
    public void Parse_with_args()
    {
        var cmd = BotCommandParser.Parse("/mute 1h", 1, null, DateTimeOffset.UtcNow);
        Assert.NotNull(cmd);
        Assert.Equal("mute", cmd.Name);
        Assert.Equal(new[] { "1h" }, cmd.Args);
    }

    [Fact]
    public void Parse_strips_bot_username_suffix()
    {
        var cmd = BotCommandParser.Parse("/ping@my_bot", 1, null, DateTimeOffset.UtcNow);
        Assert.NotNull(cmd);
        Assert.Equal("ping", cmd.Name);
    }

    [Fact]
    public void Parse_lowercases_command()
    {
        var cmd = BotCommandParser.Parse("/Ping", 1, null, DateTimeOffset.UtcNow);
        Assert.NotNull(cmd);
        Assert.Equal("ping", cmd.Name);
    }

    [Fact]
    public void Parse_returns_null_for_non_command()
    {
        Assert.Null(BotCommandParser.Parse("hello", 1, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parse_returns_null_for_empty()
    {
        Assert.Null(BotCommandParser.Parse("", 1, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parse_returns_null_for_too_long()
    {
        var longText = "/" + new string('x', 3000);
        Assert.Null(BotCommandParser.Parse(longText, 1, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parse_handles_multiple_args()
    {
        var cmd = BotCommandParser.Parse("/events system 10", 1, null, DateTimeOffset.UtcNow);
        Assert.NotNull(cmd);
        Assert.Equal("events", cmd.Name);
        Assert.Equal(new[] { "system", "10" }, cmd.Args);
    }

    [Fact]
    public void Parse_handles_slash_only()
    {
        Assert.Null(BotCommandParser.Parse("/", 1, null, DateTimeOffset.UtcNow));
    }
}
