using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Core.Notifications;
using Xunit;

namespace LocalOpsBot.Tests.Core.Notifications;

public sealed class BootMessageFormatterTests
{
    [Fact]
    public void Format_contains_machine_name()
    {
        var host = new HostInfoRecord("MY-PC", "10.0.0.5", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), null);
        var (title, body) = BootMessageFormatter.Compose(host, DateTimeOffset.UtcNow);
        var msg = title + " " + body;

        Assert.Contains("MY-PC", msg);
    }

    [Fact]
    public void Format_contains_ip()
    {
        var host = new HostInfoRecord("PC", "10.0.0.5", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), null);
        var (title, body) = BootMessageFormatter.Compose(host, DateTimeOffset.UtcNow);
        var msg = title + " " + body;

        Assert.Contains("10.0.0.5", msg);
    }

    [Fact]
    public void Format_contains_uptime()
    {
        var host = new HostInfoRecord("PC", null, DateTimeOffset.UtcNow, TimeSpan.FromDays(1), null);
        var (title, body) = BootMessageFormatter.Compose(host, DateTimeOffset.UtcNow);
        var msg = title + " " + body;

        Assert.Contains("1d", msg);
    }

    [Fact]
    public void Format_uses_unknown_when_ip_null()
    {
        var host = new HostInfoRecord("PC", null, DateTimeOffset.UtcNow, TimeSpan.Zero, null);
        var (title, body) = BootMessageFormatter.Compose(host, DateTimeOffset.UtcNow);
        var msg = title + " " + body;

        Assert.Contains("unknown", msg);
    }

    // HTML escaping moved from the formatter to the Telegram renderer when boot notifications started
    // going through the router (the phone shows plain text, M-19); the guarantee is checked there.
    [Fact]
    public void Telegram_rendering_escapes_html_in_the_machine_name()
    {
        var host = new HostInfoRecord("<script>alert('xss')</script>", "1.2.3.4", DateTimeOffset.UtcNow, TimeSpan.Zero, null);
        var (title, body) = BootMessageFormatter.Compose(host, DateTimeOffset.UtcNow);
        var rendered = new LocalOpsBot.Infrastructure.Telegram.TelegramNotificationRenderer().Render(
            new LocalOpsBot.Core.Delivery.OutboundNotification(Guid.NewGuid(), "boot",
                LocalOpsBot.Core.Delivery.OutboundPriority.Info, title, body, "boot",
                LocalOpsBot.Protocol.Messaging.MessageSensitivity.Normal,
                LocalOpsBot.Core.Delivery.DeliveryPolicy.TelegramFallback, DateTimeOffset.UtcNow));

        Assert.DoesNotContain("<script>", rendered);
        Assert.Contains("&lt;", rendered);
    }
}
