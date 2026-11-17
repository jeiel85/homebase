using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using LocalOpsBot.Core.Updates;
using LocalOpsBot.Tray.Services;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// The update flow's windows when the download fails (issue #71, regression of v0.39.1): the result
/// notice replaces the progress window instead of opening behind it, and an unattended install
/// shows no notice at all.
/// </summary>
public sealed class UpdateFlowWindowTests
{
    private static readonly UpdateInfo Release = new(
        new Version(9, 9, 9), "https://updates.invalid/Homebase.zip", null, "", DateTimeOffset.UtcNow);

    /// <summary>Open windows when the notice appeared.</summary>
    private sealed record AtNotice(bool ProgressOpen, bool NoticeEnabled, int OtherVisible);

    [Fact]
    public Task Failed_download_closes_the_progress_window_before_the_notice() => Wpf.Run(async () =>
    {
        AtNotice? seen = null;
        var poll = Watch(dialog =>
        {
            var visible = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w != dialog).ToList();
            seen = new AtNotice(visible.OfType<UpdateProgressWindow>().Any(), dialog.IsEnabled, visible.Count);
            dialog.Close();
        });

        var outcome = await Coordinator().DownloadAndApplyAsync(Release, null, unattended: false);
        poll.Stop();

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.NotNull(seen);
        // v0.39.1's bug: the progress window was still open (and disabled by the modal notice under it).
        Assert.False(seen.ProgressOpen);
        Assert.True(seen.NoticeEnabled);
        Assert.Equal(0, seen.OtherVisible);
        Assert.DoesNotContain(Application.Current.Windows.OfType<Window>(), w => w.IsVisible);
    });

    [Fact]
    public Task Unattended_failure_shows_no_notice_and_records_the_outcome() => Wpf.Run(async () =>
    {
        File.Delete(UpdateAutoOutcome.FilePath);
        var noticeShown = false;
        var poll = Watch(dialog =>
        {
            noticeShown = true;
            dialog.Close(); // so a regression fails the test instead of hanging it
        });

        var outcome = await Coordinator().DownloadAndApplyAsync(Release, null, unattended: true);
        poll.Stop();

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.False(noticeShown);
        Assert.DoesNotContain(Application.Current.Windows.OfType<Window>(), w => w.IsVisible);
        Assert.Equal(AutoInstallResult.Failed, UpdateAutoOutcome.Read()?.Result);
    });

    private static UpdateCoordinator Coordinator() =>
        new(new UpdateService(new HttpClient(new NotFound())));

    /// <summary>Calls <paramref name="onNotice"/> once, on the UI thread, when a notice is up.</summary>
    private static DispatcherTimer Watch(Action<MessageDialog> onNotice)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<MessageDialog>().FirstOrDefault(w => w.IsVisible) is not { } dialog) return;
            timer.Stop();
            onNotice(dialog);
        };
        timer.Start();
        return timer;
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }
}
