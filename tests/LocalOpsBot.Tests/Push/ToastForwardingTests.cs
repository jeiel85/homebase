using LocalOpsBot.Agent.Services;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Tests.Mobile;
using LocalOpsBot.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Push;

public class ToastBundlerTests
{
    private readonly ManualTimeProvider _time = new();

    private ToastItem Toast(string app, string title) => new(app, title, "body", _time.GetUtcNow());

    [Fact]
    public void First_toast_goes_out_at_once_and_the_rest_of_the_window_is_bundled()
    {
        var bundler = new ToastBundler(TimeSpan.FromSeconds(60), 100, _time);

        Assert.NotNull(bundler.Add(Toast("KakaoTalk", "1")));
        Assert.Null(bundler.Add(Toast("KakaoTalk", "2")));
        Assert.Null(bundler.Add(Toast("KakaoTalk", "3")));
        Assert.Empty(bundler.Flush()); // window still open

        _time.Advance(TimeSpan.FromSeconds(60));
        var batch = Assert.Single(bundler.Flush());
        Assert.Equal(2, batch.Count);
        Assert.Equal(new[] { "2", "3" }, batch.Items.Select(i => i.Title));
    }

    [Fact]
    public void Toasts_held_in_an_expired_window_go_out_with_the_next_toast_before_flush()
    {
        var bundler = new ToastBundler(TimeSpan.FromSeconds(60), 100, _time);
        bundler.Add(Toast("KakaoTalk", "t0"));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(bundler.Add(Toast("KakaoTalk", "t30")));
        _time.Advance(TimeSpan.FromSeconds(30)); // window over, Flush() not run yet

        var batch = bundler.Add(Toast("KakaoTalk", "t60"));

        Assert.NotNull(batch);
        Assert.Equal(2, batch!.Count);
        Assert.Equal(new[] { "t30", "t60" }, batch.Items.Select(i => i.Title));
        Assert.Empty(bundler.Flush());
    }

    [Fact]
    public void Apps_have_independent_windows()
    {
        var bundler = new ToastBundler(TimeSpan.FromSeconds(60), 100, _time);
        Assert.NotNull(bundler.Add(Toast("KakaoTalk", "a")));
        Assert.NotNull(bundler.Add(Toast("Outlook", "b")));
    }

    [Fact]
    public void A_sustained_stream_costs_one_push_per_window()
    {
        var bundler = new ToastBundler(TimeSpan.FromSeconds(60), 1000, _time);
        var pushes = 0;
        for (var second = 0; second < 600; second++)
        {
            if (bundler.Add(Toast("Slack", second.ToString())) is not null) pushes++;
            pushes += bundler.Flush().Count;
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.InRange(pushes, 10, 11); // 10 minutes / 60 s windows
    }

    [Fact]
    public void A_quiet_window_closes_so_the_next_toast_is_immediate_again()
    {
        var bundler = new ToastBundler(TimeSpan.FromSeconds(60), 100, _time);
        bundler.Add(Toast("Mail", "a"));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(bundler.Flush());
        Assert.NotNull(bundler.Add(Toast("Mail", "b")));
    }

    [Fact]
    public void Bundles_keep_only_the_newest_items_but_count_all()
    {
        var bundler = new ToastBundler(TimeSpan.FromSeconds(60), 100, _time);
        bundler.Add(Toast("Mail", "first"));
        for (var i = 0; i < 12; i++) bundler.Add(Toast("Mail", $"m{i}"));
        _time.Advance(TimeSpan.FromMinutes(1));

        var batch = Assert.Single(bundler.Flush());
        Assert.Equal(12, batch.Count);
        Assert.Equal(ToastBundler.MaxItemsPerBundle, batch.Items.Count);
        Assert.Equal("m11", batch.Items[^1].Title);
    }

    [Fact]
    public void Daily_budget_drops_and_counts_until_24h_have_passed()
    {
        var bundler = new ToastBundler(TimeSpan.Zero, 3, _time);
        for (var i = 0; i < 3; i++) Assert.NotNull(bundler.Add(Toast($"app{i}", "x")));
        Assert.Null(bundler.Add(Toast("app9", "x")));
        Assert.Equal(1, bundler.DroppedOverBudget);

        _time.Advance(TimeSpan.FromHours(24));
        Assert.NotNull(bundler.Add(Toast("app9", "x")));
    }
}

public class NotificationForwardingServiceTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingRouter _router = new();

    private NotificationForwardingService Create(bool telegramFallback = false) => new(
        new NullBridge(), _router, new PassThroughMasker(),
        new ToastForwardingOptions(BundleSeconds: 60, MaxPushesPerDay: 150, TelegramFallback: telegramFallback),
        NullLogger<NotificationForwardingService>.Instance, _time);

    private ToastNotificationEvent Toast(string app, string title, NotificationSensitivity s = NotificationSensitivity.Normal) =>
        new(Guid.NewGuid().ToString(), app, title, "hello", _time.GetUtcNow(), "raw", s);

    [Fact]
    public async Task Toasts_use_encrypted_channels_only_by_default()
    {
        await Create().AcceptAsync(Toast("KakaoTalk", "Mom"));

        var sent = Assert.Single(_router.Sent);
        Assert.Equal(DeliveryPolicy.EncryptedOnly, sent.Policy);
        Assert.Equal("KakaoTalk: Mom", sent.Title);
        Assert.Equal("KakaoTalk", sent.Metadata!["sourceApp"]);
        Assert.Equal("1", sent.Metadata!["count"]);
    }

    [Fact]
    public async Task Telegram_fallback_is_opt_in()
    {
        await Create(telegramFallback: true).AcceptAsync(Toast("KakaoTalk", "Mom"));
        Assert.Equal(DeliveryPolicy.LocalPreferred, Assert.Single(_router.Sent).Policy);
    }

    [Fact]
    public async Task Blocked_toasts_are_dropped()
    {
        await Create().AcceptAsync(Toast("1Password", "code", NotificationSensitivity.Blocked));
        Assert.Empty(_router.Sent);
    }

    [Fact]
    public async Task Bursts_from_one_app_become_one_bundle()
    {
        using var _ = new CultureScope("en");
        var service = Create();
        await service.AcceptAsync(Toast("Slack", "a"));
        await service.AcceptAsync(Toast("Slack", "b"));
        await service.AcceptAsync(Toast("Slack", "c"));
        Assert.Single(_router.Sent);

        _time.Advance(TimeSpan.FromSeconds(60));
        await service.FlushAsync();

        Assert.Equal(2, _router.Sent.Count);
        var bundle = _router.Sent[1];
        Assert.Equal("Slack: 2 notifications", bundle.Title);
        Assert.Equal("b: hello\nc: hello", bundle.Body);
        Assert.Equal("2", bundle.Metadata!["count"]);
    }

    private sealed class RecordingRouter : IOutboundRouter
    {
        public List<OutboundNotification> Sent { get; } = new();

        public Task<OutboundDeliveryResult> DeliverAsync(OutboundNotification notification, CancellationToken ct)
        {
            Sent.Add(notification);
            return Task.FromResult(new OutboundDeliveryResult(true, [new OutboundAttemptResult("unifiedpush", true)]));
        }
    }

    private sealed class NullBridge : INotificationBridgeServer
    {
#pragma warning disable CS0067
        public event Action<ToastNotificationEvent>? NotificationReceived;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PassThroughMasker : ITextMasker
    {
        public string Mask(string input) => input;
    }
}
