using LocalOpsBot.Agent.Services;
using LocalOpsBot.Data.Repositories;
using LocalOpsBot.Infrastructure.Telegram;
using LocalOpsBot.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Agent.Services;

public sealed class BootNotificationServiceTests
{
    private static IOptions<TelegramOptions> Opts(params long[] chatIds)
        => Options.Create(new TelegramOptions { AllowedChatIds = chatIds });

    private static BootNotificationService Create(
        ITelegramClient telegram,
        IRuntimeStateRepository? stateRepo = null,
        IOptions<TelegramOptions>? opts = null,
        List<TimeSpan>? delays = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(
            telegram,
            new FakeHostInfoProvider(),
            stateRepo ?? new FakeRuntimeStateRepository(),
            opts ?? Opts(42L),
            NullLogger<BootNotificationService>.Instance,
            delay ?? ((wait, _) => { delays?.Add(wait); return Task.CompletedTask; }));

    /// <summary>Starts the service and waits for the background send to finish.</summary>
    private static async Task RunAsync(BootNotificationService service)
    {
        await service.StartAsync(default);
        await service.ExecuteTask!;
    }

    [Fact]
    public async Task Sends_boot_notification()
    {
        var telegram = new FakeTelegramClient();
        await RunAsync(Create(telegram));

        Assert.Single(telegram.Sent);
        Assert.Equal(42, telegram.Sent[0].ChatId);
        Assert.Contains("TEST-PC", telegram.Sent[0].Text);
    }

    [Fact]
    public async Task Skips_when_no_target_chat()
    {
        var telegram = new FakeTelegramClient();
        await RunAsync(Create(telegram, opts: Opts()));

        Assert.Empty(telegram.Sent);
    }

    [Fact]
    public async Task Dedups_within_window()
    {
        var telegram = new FakeTelegramClient();
        var service = Create(telegram);

        await RunAsync(service);
        await RunAsync(service);

        Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task Retries_until_network_is_ready()
    {
        // Boot-time failure mode: DNS is not up yet for the first few attempts.
        var telegram = new FlakyTelegramClient(failuresBeforeSuccess: 3);
        var stateRepo = new FakeRuntimeStateRepository();
        var delays = new List<TimeSpan>();

        await RunAsync(Create(telegram, stateRepo, delays: delays));

        Assert.Equal(4, telegram.Attempts);
        Assert.Single(telegram.Sent);
        Assert.Equal(new[] { 5, 10, 20 }, delays.Select(d => (int)d.TotalSeconds));
        Assert.NotNull(await stateRepo.GetAsync("boot.last_notification_at", default));
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_without_crashing()
    {
        var telegram = new FlakyTelegramClient(failuresBeforeSuccess: int.MaxValue);
        var stateRepo = new FakeRuntimeStateRepository();
        var delays = new List<TimeSpan>();

        await RunAsync(Create(telegram, stateRepo, delays: delays));

        Assert.Equal(BootNotificationService.MaxAttempts, telegram.Attempts);
        Assert.Equal(BootNotificationService.MaxAttempts - 1, delays.Count);
        Assert.Empty(telegram.Sent);
        Assert.Null(await stateRepo.GetAsync("boot.last_notification_at", default));
    }

    [Fact]
    public void Retry_delay_backs_off_and_caps_at_60s()
    {
        var schedule = Enumerable.Range(1, 7).Select(a => (int)BootNotificationService.RetryDelay(a).TotalSeconds);
        Assert.Equal(new[] { 5, 10, 20, 40, 60, 60, 60 }, schedule);
    }

    [Fact]
    public async Task Start_does_not_block_host_while_retrying_and_stop_ends_cleanly()
    {
        var telegram = new FlakyTelegramClient(failuresBeforeSuccess: int.MaxValue);
        var service = Create(telegram, delay: Task.Delay); // real wait: 5s before the 2nd attempt

        await service.StartAsync(default);
        Assert.False(service.ExecuteTask!.IsCompleted);

        await service.StopAsync(default);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(1, telegram.Attempts);
    }

    [Fact]
    public async Task State_read_failure_still_sends()
    {
        var telegram = new FakeTelegramClient();
        await RunAsync(Create(telegram, stateRepo: new BrokenStateRepository()));

        Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task State_write_failure_after_send_does_not_resend()
    {
        var telegram = new FakeTelegramClient();
        var delays = new List<TimeSpan>();
        await RunAsync(Create(telegram, stateRepo: new BrokenStateRepository(), delays: delays));

        Assert.Single(telegram.Sent);
        Assert.Empty(delays);
    }

    [Fact]
    public void Container_resolves_service_without_a_registered_delay()
    {
        // Program.cs registers it via AddHostedService; the optional delay must fall back to its default.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITelegramClient>(new FakeTelegramClient());
        services.AddSingleton<LocalOpsBot.Core.Monitoring.IHostInfoProvider>(new FakeHostInfoProvider());
        services.AddSingleton<IRuntimeStateRepository>(new FakeRuntimeStateRepository());
        services.AddSingleton(Opts(42L));
        services.AddHostedService<BootNotificationService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsType<BootNotificationService>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    private sealed class FlakyTelegramClient(int failuresBeforeSuccess) : FakeTelegramClient
    {
        public int Attempts { get; private set; }

        public override Task SendMessageAsync(long chatId, string text, TelegramSendOptions? options, CancellationToken ct)
        {
            Attempts++;
            return Attempts <= failuresBeforeSuccess
                ? Task.FromException(new HttpRequestException("No such host is known. (api.telegram.org:443)"))
                : base.SendMessageAsync(chatId, text, options, ct);
        }
    }

    private sealed class BrokenStateRepository : IRuntimeStateRepository
    {
        public Task<string?> GetAsync(string key, CancellationToken ct)
            => Task.FromException<string?>(new InvalidOperationException("database is locked"));

        public Task SetAsync(string key, string value, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("database is locked"));
    }
}
