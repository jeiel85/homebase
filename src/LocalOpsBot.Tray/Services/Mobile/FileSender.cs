using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Sends files from this PC (DECISIONS M-65): the tray opens each one with the user's rights and streams it to the Agent
/// over <c>\\.\pipe\Homebase.Transfer</c> (write mode); the Agent keeps a copy and sends it on — a push to phones (they
/// fetch it), an upload to other PCs — and reports per target with <c>files.progress</c>.
/// </summary>
internal static class FileSender
{
    private const int BufferBytes = 256 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>One file to send: what to call it and how to read it.</summary>
    public sealed record Item(string Name, long Size, string? Mime, Func<Stream> Open);

    /// <summary>The send's id, or why there is none (a <see cref="TransferErrors"/> code, or <c>agent</c>/<c>failed</c>).</summary>
    public sealed record Result(string? Id, string? Error);

    /// <summary>Files on disk as items; folders and files that can't be read are left out.</summary>
    public static IReadOnlyList<Item> FromPaths(IEnumerable<string> paths)
    {
        var items = new List<Item>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue; // a folder, or gone
                var full = info.FullName;
                items.Add(new Item(info.Name, info.Length, MimeOf(info.Name),
                    () => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, useAsync: true)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return items;
    }

    /// <summary>Where files can go now (phones that fetch files, linked PCs); null when the Agent can't say.</summary>
    public static async Task<IReadOnlyList<ClipTarget>?> TargetsAsync()
    {
        if (MobileHost.Client is not { IsConnected: true } client) return null;
        try
        {
            var reply = await client.RequestAsync(MobileBridge.OpFilesTargets);
            if ((bool?)reply["ok"] != true) return null;
            return reply["payload"]?.Deserialize<ClipTargetList>(MobileProtocol.Json)?.Targets ?? Array.Empty<ClipTarget>();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Hands <paramref name="items"/> to the Agent for <paramref name="targetKeys"/> and starts the send.</summary>
    public static async Task<Result> SendAsync(IReadOnlyList<Item> items, IReadOnlyList<string> targetKeys, CancellationToken ct = default)
    {
        if (MobileHost.Client is not { IsConnected: true } client) return new(null, "agent");
        var files = items.Select(i => new TransferFile(i.Name, i.Size, i.Mime)).ToList();
        if (TransferProtocol.Check(new TransferMeta(MobileProtocol.Schema, files, DateTimeOffset.UtcNow)) is { } problem) return new(null, problem);

        string id;
        try
        {
            var begun = await client.RequestAsync(MobileBridge.OpFilesBegin, new FilesBeginRequest(targetKeys, files), ct);
            if ((bool?)begun["ok"] != true) return new(null, "agent");
            var result = begun["payload"]?.Deserialize<FilesBeginResult>(MobileProtocol.Json);
            if (result?.Id is not { } begunId) return new(null, result?.Error ?? "failed");
            id = begunId;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            return new(null, "agent");
        }

        try
        {
            for (var i = 0; i < items.Count; i++) await WriteOneAsync(id, i, items[i], ct);
            var go = await client.RequestAsync(MobileBridge.OpFilesGo, new FilesIdRequest(id), ct);
            if ((bool?)go["ok"] != true) throw new IOException("The Agent did not start the send.");
            return new(id, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException
                                       or InvalidDataException or TimeoutException)
        {
            TrayLog.Warn("Files", $"Sending {items.Count} file(s) failed: {ex.GetType().Name}: {ex.Message}");
            try { await client.RequestAsync(MobileBridge.OpFilesCancel, new FilesIdRequest(id)); }
            catch (Exception cancel) when (cancel is IOException or OperationCanceledException) { }
            return new(null, ex is UnauthorizedAccessException or FileNotFoundException ? "unreadable" : "failed");
        }
    }

    /// <summary>One file over the pipe: a request, the Agent's go-ahead, exactly <see cref="Item.Size"/> bytes, its receipt.</summary>
    private static async Task WriteOneAsync(string id, int index, Item item, CancellationToken ct)
    {
        await using var source = item.Open();
        await using var pipe = new NamedPipeClientStream(".", TransferPipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
        var request = (JsonObject)JsonSerializer.SerializeToNode(
            new TransferPipeRequest(id, index, TransferPipeRequest.Write, item.Size), MobileProtocol.Json)!;
        await MobileBridge.WriteFrameAsync(pipe, request, ct);
        var ready = (await MobileBridge.ReadFrameAsync(pipe, ct))?.Deserialize<TransferPipeHeader>(MobileProtocol.Json);
        if (ready is not { Ok: true }) throw new IOException($"The Agent did not take file {index} ({ready?.Error}).");

        var buffer = new byte[BufferBytes];
        var left = item.Size;
        while (left > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (read == 0) throw new IOException($"{item.Name} got shorter while it was being sent.");
            await pipe.WriteAsync(buffer.AsMemory(0, read), ct);
            left -= read;
        }
        await pipe.FlushAsync(ct);
        var stored = (await MobileBridge.ReadFrameAsync(pipe, ct))?.Deserialize<TransferPipeHeader>(MobileProtocol.Json);
        if (stored is not { Ok: true }) throw new IOException($"The Agent did not store file {index}.");
    }

    /// <summary>A MIME type from the extension, for the phone to pick where to save it (a picture goes to the gallery).</summary>
    public static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".txt" or ".log" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".hwp" => "application/x-hwp",
        ".apk" => "application/vnd.android.package-archive",
        _ => "application/octet-stream",
    };
}
