using System.IO;
using System.Xml.Linq;
using LocalOpsBot.Tray.Services.Mobile;
using Windows.UI.Notifications;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// The "files received" toast (DECISIONS M-67): what it shows, and that its homebase: links only ever reach a received
/// file by its plain name in the received folder (they can be typed into a browser by anyone).
/// </summary>
public class ReceivedToastTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-toast-" + Guid.NewGuid().ToString("N"));

    public ReceivedToastTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void One_picture_shows_in_the_toast_with_open_and_open_folder()
    {
        var photo = Path.Combine(_dir, "IMG 0412.jpg");
        File.WriteAllBytes(photo, new byte[1024]);

        var toast = XElement.Parse(ReceivedToast.Xml("Galaxy S24 <파일>", "a & b", [photo], picture: true));

        Assert.Equal("protocol", toast.Attribute("activationType")!.Value);
        Assert.Equal("homebase://files/show?name=IMG%200412.jpg", toast.Attribute("launch")!.Value);
        Assert.Equal(new[] { "Galaxy S24 <파일>", "a & b" }, toast.Descendants("text").Select(t => t.Value));
        Assert.Equal(new Uri(photo).AbsoluteUri, toast.Descendants("image").Single().Attribute("src")!.Value);
        Assert.Equal(new[] { "homebase://files/open?name=IMG%200412.jpg", "homebase://files/folder" },
            toast.Descendants("action").Select(a => a.Attribute("arguments")!.Value));
    }

    [Fact]
    public void Several_files_open_the_folder_and_show_no_picture()
    {
        var a = Path.Combine(_dir, "a.jpg");
        var b = Path.Combine(_dir, "b.pdf");
        File.WriteAllBytes(a, new byte[10]);
        File.WriteAllBytes(b, new byte[10]);
        var big = Path.Combine(_dir, "big.png");
        File.WriteAllBytes(big, new byte[3 * 1024 * 1024 + 1]);

        var toast = XElement.Parse(ReceivedToast.Xml("t", "b", [a, b], picture: true));
        Assert.Equal("homebase://files/folder", toast.Attribute("launch")!.Value);
        Assert.Empty(toast.Descendants("image"));
        Assert.Equal(new[] { "homebase://files/folder" }, toast.Descendants("action").Select(x => x.Attribute("arguments")!.Value));
        Assert.Empty(XElement.Parse(ReceivedToast.Xml("t", "b", [big], picture: true)).Descendants("image")); // over 3 MB
    }

    [Theory]
    [InlineData("homebase://files/open?name=IMG%200412.jpg", "open", "IMG 0412.jpg")]
    [InlineData("homebase://files/folder", "folder", null)]
    [InlineData("homebase://files/show?x=1&name=a.pdf", "show", "a.pdf")]
    public void Our_links_are_read(string link, string action, string? name) =>
        Assert.Equal((action, name), ReceivedToast.Parse(link));

    [Theory]
    [InlineData("homebase://files/delete?name=a.pdf")]
    [InlineData("homebase://other/open?name=a.pdf")]
    [InlineData("https://files/open?name=a.pdf")]
    [InlineData("not a link")]
    public void Other_links_are_ignored(string link) => Assert.Null(ReceivedToast.Parse(link));

    [Fact]
    public void Only_a_received_file_by_its_plain_name_is_reached()
    {
        File.WriteAllText(Path.Combine(_dir, "a.pdf"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.pdf"), "x");

        Assert.Equal(Path.Combine(_dir, "a.pdf"), ReceivedToast.Received(_dir, "a.pdf"), ignoreCase: true);
        Assert.Null(ReceivedToast.Received(_dir, "missing.pdf"));
        Assert.Null(ReceivedToast.Received(_dir, @"sub\b.pdf"));
        Assert.Null(ReceivedToast.Received(_dir, @"..\a.pdf"));
        Assert.Null(ReceivedToast.Received(_dir, @"C:\Windows\notepad.exe"));
        Assert.Null(ReceivedToast.Received(_dir, ".."));
        Assert.Null(ReceivedToast.Received(_dir, null));
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("run.BAT", true)]
    [InlineData("script.ps1", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("report.pdf", false)]
    public void Programs_and_scripts_are_shown_rather_than_run(string name, bool runnable) =>
        Assert.Equal(runnable, ReceivedToast.IsRunnable(name));

    [Fact]
    public void Only_enabled_lets_the_toast_show_and_every_block_says_why()
    {
        // Windows drops a toast it won't show without a word; a block makes the tray show a balloon and log the reason.
        Assert.Null(ReceivedToast.Blocked(NotificationSetting.Enabled));
        foreach (var setting in Enum.GetValues<NotificationSetting>().Where(s => s != NotificationSetting.Enabled))
            Assert.False(string.IsNullOrWhiteSpace(ReceivedToast.Blocked(setting)), setting.ToString());
        Assert.Contains("Settings", ReceivedToast.Blocked(NotificationSetting.DisabledForApplication));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
