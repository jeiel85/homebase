using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using LocalOpsBot.Tray.Services.Mobile;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// More ways in to sending files (DECISIONS M-66): Explorer's "Send to" (the shortcut, <c>--send</c> and the pipe to the
/// running tray) and folders or many files sent as one zip.
/// </summary>
public class SendEntrancesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-send3a-" + Guid.NewGuid().ToString("N"));

    public SendEntrancesTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Send_to_passes_full_paths_once_each()
    {
        var paths = SendRequests.PathsFrom(["--send", @"C:\a.txt", "relative.txt", "\"C:\\b c.txt\"", @"C:\A.TXT", "  "]);

        Assert.Equal(new[] { @"C:\a.txt", @"C:\b c.txt" }, paths);
        Assert.Null(SendRequests.PathsFrom(["--preview-dashboard"]));
        Assert.Empty(SendRequests.PathsFrom(["--send"])!);
    }

    [Fact]
    public async Task The_pipe_takes_one_path_list_and_refuses_an_oversized_one()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new[] { @"C:\photos\a.jpg", "not-full" });
        Assert.Equal(new[] { @"C:\photos\a.jpg" }, await SendRequests.ReadAsync(Frame(json.Length, json), CancellationToken.None));

        Assert.Null(await SendRequests.ReadAsync(Frame(SendRequests.MaxFrameBytes + 1, []), CancellationToken.None));
        Assert.Null(await SendRequests.ReadAsync(Frame(0, []), CancellationToken.None));
    }

    [Fact]
    public async Task A_second_start_hands_its_paths_to_the_running_tray()
    {
        var name = "Homebase.Tray.Send.Test." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var got = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(() => SendRequests.ServeAsync(paths => got.TrySetResult(paths), stop.Token, name));

        var forwarded = false;
        for (var i = 0; i < 20 && !forwarded; i++) // the server may not be listening yet
            forwarded = await Task.Run(() => SendRequests.Forward([@"C:\x\one.pdf", @"D:\two.png"], TimeSpan.FromSeconds(1), name));

        Assert.True(forwarded);
        Assert.Equal(new[] { @"C:\x\one.pdf", @"D:\two.png" }, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        stop.Cancel();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(SendRequests.Forward([@"C:\a.txt"], TimeSpan.FromMilliseconds(200), name)); // nobody there
    }

    [Fact]
    public void The_send_to_shortcut_is_kept_in_step_and_someone_elses_is_left_alone()
    {
        var link = Path.Combine(_dir, "SendTo", "Homebase.lnk");
        var exe = Touch(Path.Combine(_dir, "v1", "Homebase.Tray.exe"));
        var moved = Touch(Path.Combine(_dir, "v2", "Homebase.Tray.exe"));

        SendToShortcut.Sync(true, exe, link);
        Assert.Equal(exe, SendToShortcut.TargetOf(link), ignoreCase: true);
        SendToShortcut.Sync(true, moved, link); // installed somewhere else since
        Assert.Equal(moved, SendToShortcut.TargetOf(link), ignoreCase: true);
        SendToShortcut.Sync(false, moved, link);
        Assert.False(File.Exists(link));

        var other = Touch(Path.Combine(_dir, "other", "notepad.exe"));
        SendToShortcut.Create(link, other);
        SendToShortcut.Sync(true, exe, link);
        Assert.Equal(other, SendToShortcut.TargetOf(link), ignoreCase: true);
        SendToShortcut.Sync(false, exe, link);
        Assert.True(File.Exists(link));
    }

    [Fact]
    public void Folders_or_more_than_twenty_files_go_as_one_zip()
    {
        var files = Enumerable.Range(1, 21).Select(i => Touch(Path.Combine(_dir, "many", $"{i}.txt"))).ToList();
        var folder = Path.Combine(_dir, "Photos");
        Touch(Path.Combine(folder, "a.jpg"));

        Assert.False(SendBundle.Classify(files.Take(20)).NeedsZip);
        Assert.True(SendBundle.Classify(files).NeedsZip);
        var one = SendBundle.Classify([folder, Path.Combine(_dir, "gone.txt")]);
        Assert.True(one.NeedsZip);
        Assert.Equal(1, one.Missing);
        Assert.Equal("Photos.zip", SendBundle.ZipName(one, new DateTime(2026, 11, 19, 15, 30, 0)));
        Assert.Equal("Homebase-20261119-153000.zip", SendBundle.ZipName(SendBundle.Classify(files), new DateTime(2026, 11, 19, 15, 30, 0)));
    }

    [Fact]
    public void A_zip_keeps_the_folder_layout_and_leaves_out_hidden_files()
    {
        var folder = Path.Combine(_dir, "Trip");
        File.WriteAllText(Touch(Path.Combine(folder, "day1", "a.jpg")), "a");
        File.WriteAllText(Touch(Path.Combine(folder, "b.jpg")), "bb");
        var hidden = Touch(Path.Combine(folder, "Thumbs.db"));
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var loose = Touch(Path.Combine(_dir, "notes", "b.jpg")); // same name as one in the folder, at the top

        var zipPath = Path.Combine(_dir, "out.zip");
        var result = SendBundle.Create(SendBundle.Classify([loose, folder]), zipPath);

        Assert.Null(result.Error);
        Assert.Equal(0, result.Skipped);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(new[] { "Trip/b.jpg", "Trip/day1/a.jpg", "b.jpg" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var reader = new StreamReader(zip.GetEntry("Trip/b.jpg")!.Open());
        Assert.Equal("bb", reader.ReadToEnd());
    }

    [Fact]
    public void A_zip_too_large_or_with_nothing_in_it_is_not_made()
    {
        var big = Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(big, new byte[4096]);
        var tooLarge = SendBundle.Create(SendBundle.Classify([big, Path.Combine(_dir, "x")]), Path.Combine(_dir, "a.zip"), maxBytes: 1024);
        Assert.Equal("too_large", tooLarge.Error);
        Assert.False(File.Exists(Path.Combine(_dir, "a.zip")));

        var empty = Path.Combine(_dir, "Empty");
        Directory.CreateDirectory(empty);
        Assert.Equal("empty", SendBundle.Create(SendBundle.Classify([empty]), Path.Combine(_dir, "b.zip")).Error);
    }

    [Fact]
    public void A_file_that_cant_be_read_is_skipped_and_counted()
    {
        var folder = Path.Combine(_dir, "Docs");
        File.WriteAllText(Touch(Path.Combine(folder, "ok.txt")), "ok");
        var locked = Touch(Path.Combine(folder, "locked.txt"));
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = SendBundle.Create(SendBundle.Classify([folder]), Path.Combine(_dir, "c.zip"));

        Assert.Null(result.Error);
        Assert.Equal(1, result.Skipped);
        using var zip = ZipFile.OpenRead(result.Path!);
        Assert.Equal("Docs/ok.txt", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public void A_folder_that_cant_be_listed_is_left_out_and_counted()
    {
        var folder = Path.Combine(_dir, "Mixed");
        File.WriteAllText(Touch(Path.Combine(folder, "ok.txt")), "ok");
        var closed = Path.Combine(folder, "closed");
        File.WriteAllText(Touch(Path.Combine(closed, "secret.txt")), "x");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var acl = new DirectoryInfo(closed).GetAccessControl();
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        acl.AddAccessRule(deny);
        new DirectoryInfo(closed).SetAccessControl(acl);
        try
        {
            var result = SendBundle.Create(SendBundle.Classify([folder]), Path.Combine(_dir, "d.zip"));

            Assert.Null(result.Error);
            Assert.Equal(1, result.Skipped); // the closed folder: told, not silently missing
            using var zip = ZipFile.OpenRead(result.Path!);
            Assert.Equal("Mixed/ok.txt", Assert.Single(zip.Entries).FullName);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            new DirectoryInfo(closed).SetAccessControl(acl);
        }
    }

    private static MemoryStream Frame(int length, byte[] body)
    {
        var stream = new MemoryStream();
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, length);
        stream.Write(head);
        stream.Write(body);
        stream.Position = 0;
        return stream;
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllBytes(path, []);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
