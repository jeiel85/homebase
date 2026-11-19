using System.IO;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services.Mobile;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>Received files (DECISIONS M-64): marked as from the internet; a single picture also goes to the clipboard.</summary>
public class FileReceiverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-recv-" + Guid.NewGuid().ToString("N"));

    public FileReceiverTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void A_received_file_carries_the_mark_of_the_web()
    {
        var path = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });

        FileReceiver.MarkFromInternet(path);

        // The temp folder is on NTFS on Windows and on the CI runner; elsewhere the mark is skipped, never the file.
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveFormat != "NTFS") return;
        Assert.Contains("ZoneId=3", File.ReadAllText(path + ":Zone.Identifier"));
        Assert.Equal(new byte[] { 0x4D, 0x5A }, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("photo.jpg", "image/jpeg", 1000L, true)]
    [InlineData("shot.PNG", null, 1000L, true)]
    [InlineData("noext", "image/webp", 1000L, true)]
    [InlineData("doc.pdf", "application/pdf", 1000L, false)]
    [InlineData("empty.png", "image/png", 0L, false)]
    [InlineData("huge.png", "image/png", 60L * 1024 * 1024, false)]
    public void Only_a_reasonable_picture_goes_to_the_clipboard(string name, string? mime, long size, bool picture) =>
        Assert.Equal(picture, FileReceiver.IsPicture(new TrayTransferFile(0, name, size, mime)));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
