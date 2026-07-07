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
        var msg = BootMessageFormatter.Format(host, DateTimeOffset.UtcNow);

        Assert.Contains("MY-PC", msg);
    }

    [Fact]
    public void Format_contains_ip()
    {
        var host = new HostInfoRecord("PC", "10.0.0.5", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), null);
        var msg = BootMessageFormatter.Format(host, DateTimeOffset.UtcNow);

        Assert.Contains("10.0.0.5", msg);
    }

    [Fact]
    public void Format_contains_uptime()
    {
        var host = new HostInfoRecord("PC", null, DateTimeOffset.UtcNow, TimeSpan.FromDays(1), null);
        var msg = BootMessageFormatter.Format(host, DateTimeOffset.UtcNow);

        Assert.Contains("1d", msg);
    }

    [Fact]
    public void Format_uses_unknown_when_ip_null()
    {
        var host = new HostInfoRecord("PC", null, DateTimeOffset.UtcNow, TimeSpan.Zero, null);
        var msg = BootMessageFormatter.Format(host, DateTimeOffset.UtcNow);

        Assert.Contains("unknown", msg);
    }

    [Fact]
    public void Format_escapes_html()
    {
        var host = new HostInfoRecord("<script>alert('xss')</script>", "1.2.3.4", DateTimeOffset.UtcNow, TimeSpan.Zero, null);
        var msg = BootMessageFormatter.Format(host, DateTimeOffset.UtcNow);

        Assert.DoesNotContain("<script>", msg);
        Assert.Contains("&lt;", msg);
    }
}
