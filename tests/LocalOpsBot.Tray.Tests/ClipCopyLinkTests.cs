using System.Windows;
using System.Windows.Documents;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// The [Copy] link on a clipboard entry of the notification center (DECISIONS M-70): it says how the copy went,
/// then reads "Copy" again instead of claiming a copy until the list is drawn again.
/// </summary>
public sealed class ClipCopyLinkTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);

    private static string Shown(Hyperlink link) => ((Run)link.Inlines.FirstInline).Text;

    private static void Click(Hyperlink link) => link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));

    [Fact]
    public Task A_copy_says_so_and_then_reads_copy_again() => Wpf.Run(async () =>
    {
        var copied = new List<string>();
        var link = NotificationCenterPanel.CopyLink("택배 6081-2345", t => { copied.Add(t); return Task.FromResult(true); }, Brief);
        Assert.Equal(L.CenterCopy, Shown(link));

        Click(link);

        Assert.Equal(new[] { "택배 6081-2345" }, copied);
        Assert.Equal(L.CenterCopied, Shown(link));
        await Task.Delay(Brief * 4);
        Assert.Equal(L.CenterCopy, Shown(link));
    });

    [Fact]
    public Task A_busy_clipboard_is_reported_and_the_link_can_be_tried_again() => Wpf.Run(async () =>
    {
        var answers = new Queue<bool>(new[] { false, true });
        var link = NotificationCenterPanel.CopyLink("x", _ => Task.FromResult(answers.Dequeue()), Brief);

        Click(link);
        Assert.Equal(L.ClipBusy, Shown(link));
        Click(link); // clicked again before it went back: the second result shows, for its own full time
        Assert.Equal(L.CenterCopied, Shown(link));
        await Task.Delay(Brief * 4);
        Assert.Equal(L.CenterCopy, Shown(link));
    });
}
