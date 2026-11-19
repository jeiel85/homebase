using LocalOpsBot.Tray.Services.Mobile;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>What a password manager's copy looks like on the Windows clipboard (DECISIONS M-62).</summary>
public class ClipboardShareTests
{
    private static Func<string, int?> Values(params (string Format, int Value)[] values) =>
        f => values.Where(v => v.Format == f).Select(v => (int?)v.Value).FirstOrDefault();

    [Fact]
    public void Plain_text_is_not_sensitive()
    {
        Assert.False(ClipboardShare.IsSensitive(new[] { "UnicodeText", "Text", "OEMText", "Locale" }, Values()));
    }

    [Theory]
    [InlineData(ClipboardShare.ExcludeMonitor)]
    [InlineData(ClipboardShare.ViewerIgnore)]
    public void A_privacy_marker_format_makes_it_sensitive(string format)
    {
        Assert.True(ClipboardShare.IsSensitive(new[] { "UnicodeText", format }, Values()));
    }

    [Theory]
    [InlineData(ClipboardShare.CanIncludeInHistory)]
    [InlineData(ClipboardShare.CanUploadToCloud)]
    public void Keeping_it_out_of_history_or_the_cloud_makes_it_sensitive(string format)
    {
        Assert.True(ClipboardShare.IsSensitive(new[] { "UnicodeText", format }, Values((format, 0))));
        // Allowed explicitly (value 1), or a value that can't be read: not a password manager's mark.
        Assert.False(ClipboardShare.IsSensitive(new[] { "UnicodeText", format }, Values((format, 1))));
        Assert.False(ClipboardShare.IsSensitive(new[] { "UnicodeText", format }, Values()));
    }
}
