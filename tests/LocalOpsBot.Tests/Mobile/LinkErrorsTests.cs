using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Which link errors mean the other PC answered but refused this PC's presence (#113).</summary>
public class LinkErrorsTests
{
    [Theory]
    [InlineData("HTTP 400", true)]
    [InlineData("HTTP 404", true)]
    [InlineData("HTTP 500", true)]
    [InlineData("HTTP 200", false)] // answered, but the body could not be read: not a refusal
    [InlineData(LinkErrors.Rejected, false)]
    [InlineData(LinkErrors.Unreachable, false)]
    [InlineData(null, false)]
    public void Only_a_non_success_status_is_a_refusal(string? error, bool refused) =>
        Assert.Equal(refused, LinkErrors.IsRefused(error));
}
