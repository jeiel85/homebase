using LocalOpsBot.Infrastructure.Commands;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure.Commands;

public sealed class AllowedChatPolicyTests
{
    [Fact]
    public void IsAllowed_allows_configured_chat()
    {
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = new[] { 100L, 200L }
        });
        var policy = new AllowedChatPolicy(opts);

        Assert.True(policy.IsAllowed(100, null));
        Assert.True(policy.IsAllowed(200, 999));
    }

    [Fact]
    public void IsAllowed_blocks_unknown_chat()
    {
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = new[] { 100L }
        });
        var policy = new AllowedChatPolicy(opts);

        Assert.False(policy.IsAllowed(999, null));
        Assert.False(policy.IsAllowed(0, 100));
    }

    [Fact]
    public void IsAllowed_empty_allowlist_blocks_all()
    {
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = Array.Empty<long>()
        });
        var policy = new AllowedChatPolicy(opts);

        Assert.False(policy.IsAllowed(1, null));
        Assert.False(policy.IsAllowed(100, null));
    }

    [Fact]
    public void IsAllowed_userId_does_not_affect_decision()
    {
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = new[] { 100L }
        });
        var policy = new AllowedChatPolicy(opts);

        Assert.True(policy.IsAllowed(100, 1));
        Assert.True(policy.IsAllowed(100, null));
        Assert.False(policy.IsAllowed(200, 100));
    }
}
