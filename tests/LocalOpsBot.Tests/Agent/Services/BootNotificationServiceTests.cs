using LocalOpsBot.Agent.Services;
using LocalOpsBot.Infrastructure.Telegram;
using LocalOpsBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Agent.Services;

public sealed class BootNotificationServiceTests
{
    [Fact]
    public async Task StartAsync_sends_boot_notification()
    {
        var telegram = new FakeTelegramClient();
        var hostInfo = new FakeHostInfoProvider();
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = new[] { 42L }
        });

        var stateRepo = new FakeRuntimeStateRepository();
        var service = new BootNotificationService(telegram, hostInfo, stateRepo, opts, NullLogger<BootNotificationService>.Instance);
        await service.StartAsync(default);

        Assert.Single(telegram.Sent);
        Assert.Equal(42, telegram.Sent[0].ChatId);
        Assert.Contains("TEST-PC", telegram.Sent[0].Text);
    }

    [Fact]
    public async Task StartAsync_skips_when_no_target_chat()
    {
        var telegram = new FakeTelegramClient();
        var hostInfo = new FakeHostInfoProvider();
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = Array.Empty<long>()
        });

        var stateRepo = new FakeRuntimeStateRepository();
        var service = new BootNotificationService(telegram, hostInfo, stateRepo, opts, NullLogger<BootNotificationService>.Instance);
        await service.StartAsync(default);

        Assert.Empty(telegram.Sent);
    }

    [Fact]
    public async Task StartAsync_dedup_within_window()
    {
        var telegram = new FakeTelegramClient();
        var hostInfo = new FakeHostInfoProvider();
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = new[] { 42L }
        });

        var stateRepo = new FakeRuntimeStateRepository();
        var service = new BootNotificationService(telegram, hostInfo, stateRepo, opts, NullLogger<BootNotificationService>.Instance);

        await service.StartAsync(default);
        await service.StartAsync(default);

        Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task StartAsync_does_not_crash_on_telegram_failure()
    {
        var telegram = new ThrowingTelegramClient();
        var hostInfo = new FakeHostInfoProvider();
        var opts = Options.Create(new TelegramOptions
        {
            AllowedChatIds = new[] { 42L }
        });

        var stateRepo = new FakeRuntimeStateRepository();
        var service = new BootNotificationService(telegram, hostInfo, stateRepo, opts, NullLogger<BootNotificationService>.Instance);
        await service.StartAsync(default);

        Assert.Empty(telegram.Sent);
    }

    private sealed class ThrowingTelegramClient : FakeTelegramClient
    {
        public override Task SendMessageAsync(long chatId, string text, TelegramSendOptions? options, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("Telegram unavailable"));
    }
}
