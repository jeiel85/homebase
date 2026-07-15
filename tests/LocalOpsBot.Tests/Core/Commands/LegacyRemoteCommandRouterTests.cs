using LocalOpsBot.Core.Commands;
using LocalOpsBot.Protocol.Messaging;
using Xunit;

namespace LocalOpsBot.Tests.Core.Commands;

public sealed class LegacyRemoteCommandRouterTests
{
    [Fact]
    public async Task RouteAsync_MapsRemoteCommandToLegacyRouter()
    {
        var legacy = new CapturingCommandRouter();
        var router = new LegacyRemoteCommandRouter(legacy);

        var command = new RemoteCommand(
            Guid.NewGuid(),
            "/ping",
            [],
            new CommandPrincipal(
                "telegram:123",
                CommandPrincipalKind.TelegramChat,
                null,
                CommandTrustLevel.Allowed),
            EndpointAddress.Telegram(123),
            "/ping",
            DateTimeOffset.UtcNow,
            LegacyChatId: 123,
            LegacyUserId: 456);

        var result = await router.RouteAsync(
            command,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(legacy.Captured);
        Assert.Equal(123, legacy.Captured!.ChatId);
        Assert.Equal(456, legacy.Captured.UserId);
    }

    private sealed class CapturingCommandRouter : ICommandRouter
    {
        public BotCommand? Captured { get; private set; }

        public Task<CommandResult> RouteAsync(
            BotCommand command,
            CancellationToken ct)
        {
            Captured = command;
            return Task.FromResult(
                new CommandResult(true, "ok", true));
        }
    }
}
