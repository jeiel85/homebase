using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Infrastructure.Notifications;
using Xunit;

namespace LocalOpsBot.Tests.Core.Notifications;

public sealed class NotificationFilterTests
{
    [Fact]
    public void Evaluate_allows_app_not_in_block_list()
    {
        var filter = new NotificationFilter("BlockList", [], ["BlockedApp"], new RegexTextMasker([]), false);
        var notification = new ToastNotificationEvent("1", "AllowedApp", "Title", "Body", DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal);
        var result = filter.Evaluate(notification);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Evaluate_blocks_app_in_block_list()
    {
        var filter = new NotificationFilter("BlockList", [], ["BlockedApp"], new RegexTextMasker([]), false);
        var notification = new ToastNotificationEvent("1", "BlockedApp", "Title", "Body", DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal);
        var result = filter.Evaluate(notification);
        Assert.False(result.Allowed);
        Assert.Equal("Blocked app", result.DropReason);
    }

    [Fact]
    public void Evaluate_blocks_app_not_in_allow_list()
    {
        var filter = new NotificationFilter("AllowList", ["TrustedApp"], [], new RegexTextMasker([]), false);
        var notification = new ToastNotificationEvent("1", "UnknownApp", "Title", "Body", DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal);
        var result = filter.Evaluate(notification);
        Assert.False(result.Allowed);
        Assert.Equal("Not in allow list", result.DropReason);
    }

    [Fact]
    public void Evaluate_allows_app_in_allow_list()
    {
        var filter = new NotificationFilter("AllowList", ["TrustedApp"], [], new RegexTextMasker([]), false);
        var notification = new ToastNotificationEvent("1", "TrustedApp", "Title", "Body", DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal);
        var result = filter.Evaluate(notification);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Evaluate_block_list_takes_precedence_over_allow_list()
    {
        var filter = new NotificationFilter("AllowList", ["TrustedApp"], ["TrustedApp"], new RegexTextMasker([]), false);
        var notification = new ToastNotificationEvent("1", "TrustedApp", "Title", "Body", DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal);
        var result = filter.Evaluate(notification);
        Assert.False(result.Allowed);
        Assert.Equal("Blocked app", result.DropReason);
    }

    [Fact]
    public void Evaluate_case_insensitive_app_name()
    {
        var filter = new NotificationFilter("BlockList", [], ["BlockedApp"], new RegexTextMasker([]), false);
        var notification = new ToastNotificationEvent("1", "blockedapp", "Title", "Body", DateTimeOffset.UtcNow, "raw", NotificationSensitivity.Normal);
        var result = filter.Evaluate(notification);
        Assert.False(result.Allowed);
    }
}
