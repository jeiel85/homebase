using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Saves files another device sent (DECISIONS M-64): the Agent checked them and keeps them in its own folder; this
/// reads each one over <c>\\.\pipe\Homebase.Transfer</c> and writes it, with the user's rights, to
/// <c>Downloads\Homebase</c> under a cleaned, unique name, marked as coming from the internet (MOTW) so Windows warns
/// before running or opening it. Nothing is opened automatically.
/// </summary>
internal static class FileReceiver
{
    private const int BufferBytes = 256 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What was saved where; <see cref="Error"/> is set when not everything was.</summary>
    public sealed record Result(IReadOnlyList<string> Paths, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>The folder received files go to: <c>Homebase</c> inside the user's Downloads (wherever they moved it).</summary>
    public static string Folder() => Path.Combine(DownloadsFolder(), "Homebase");

    /// <summary>Saves every file of <paramref name="transfer"/>; a file that fails is removed and the rest still saved.</summary>
    public static async Task<Result> SaveAsync(TrayTransfer transfer, CancellationToken ct = default)
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        string? error = null;
        foreach (var file in transfer.Files)
        {
            try
            {
                paths.Add(await SaveOneAsync(transfer.Id, file, folder, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or TimeoutException)
            {
                error ??= ex.Message;
                TrayLog.Warn("Files", $"Saving file {file.Index} of {transfer.Id} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return new Result(paths, error);
    }

    private static async Task<string> SaveOneAsync(string id, TrayTransferFile file, string folder, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", TransferPipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
        // Both frames are bare objects (not bridge {op, payload} messages): the request, then the Agent's header.
        var request = (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(new TransferPipeRequest(id, file.Index), MobileProtocol.Json)!;
        await MobileBridge.WriteFrameAsync(pipe, request, ct);
        var header = (await MobileBridge.ReadFrameAsync(pipe, ct))?.Deserialize<TransferPipeHeader>(MobileProtocol.Json)
                     ?? throw new InvalidDataException("No header from the Agent.");
        if (!header.Ok) throw new IOException($"The Agent has no such file ({header.Error}).");

        var (created, path) = CreateUnique(folder, file.Name);
        try
        {
            await using (var output = created)
            {
                var buffer = new byte[BufferBytes];
                long left = header.Size;
                while (left > 0)
                {
                    var read = await pipe.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                    if (read == 0) throw new EndOfStreamException($"The file ended {left} bytes early.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    left -= read;
                }
            }
            MarkFromInternet(path);
            return path;
        }
        catch
        {
            // Only the file this call created (and has closed by now): never one that was there before.
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// A new, empty file in <paramref name="folder"/> under the cleaned <paramref name="name"/> — "name (2).ext" and on
    /// when taken. <see cref="FileMode.CreateNew"/> never overwrites: when another save (or another program) takes the
    /// name between the check and the create, the next free name is tried.
    /// </summary>
    internal static (FileStream Stream, string Path) CreateUnique(string folder, string? name)
    {
        var clean = TransferNames.Clean(name);
        for (var attempt = 0; ; attempt++)
        {
            var path = Path.Combine(folder, TransferNames.Unique(clean, n => File.Exists(Path.Combine(folder, n))));
            try
            {
                return (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, useAsync: true), path);
            }
            catch (IOException) when (attempt < 20 && File.Exists(path))
            {
                // Taken a moment ago: the next round finds it taken and picks the next number.
            }
        }
    }

    /// <summary>
    /// The Mark of the Web: Windows (SmartScreen, Office's Protected View) treats the file as downloaded from the
    /// internet. A drive without alternate data streams (FAT, exFAT) can't hold it; the file is kept anyway.
    /// </summary>
    internal static void MarkFromInternet(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TrayLog.Info("Files", $"No internet mark on {Path.GetFileName(path)} ({ex.GetType().Name}).");
        }
    }

    /// <summary>Whether a received file is a picture the tray can also put on the clipboard (one picture, not huge).</summary>
    public static bool IsPicture(TrayTransferFile file) =>
        file.Size is > 0 and <= 50L * 1024 * 1024
        && ((file.Mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
            || Path.GetExtension(file.Name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic");

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
            {
                try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } path) return path; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
