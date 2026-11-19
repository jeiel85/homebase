using System.IO;
using System.IO.Compression;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Folders, or more files than one send takes, go as one zip (DECISIONS M-66). The tray writes it with the user's rights in
/// <c>%TEMP%\Homebase\send\&lt;id&gt;</c>, hands it to the Agent like any file and deletes it; the other end just gets a zip.
/// </summary>
internal static class SendBundle
{
    /// <summary>What the user picked: files, folders, and how many paths were neither (gone since).</summary>
    public sealed record Selection(IReadOnlyList<string> Files, IReadOnlyList<string> Folders, int Missing)
    {
        /// <summary>A folder, or more files than one send takes (<see cref="TransferProtocol.MaxFiles"/>).</summary>
        public bool NeedsZip => Folders.Count > 0 || Files.Count > TransferProtocol.MaxFiles;
    }

    /// <summary>
    /// The zip, how many files or folders could not be read into it (they are left out, and the user is told), or why
    /// there is none (<c>too_large</c>, <c>empty</c>, <c>failed</c>).
    /// </summary>
    public sealed record Result(string? Path, int Skipped, string? Error);

    public static string TempRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Homebase", "send");

    public static Selection Classify(IEnumerable<string> paths)
    {
        var files = new List<string>();
        var folders = new List<string>();
        var missing = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) folders.Add(path);
            else if (File.Exists(path)) files.Add(path);
            else missing++;
        }
        return new Selection(files, folders, missing);
    }

    /// <summary>"Photos.zip" for one folder alone, else "Homebase-20261119-153000.zip".</summary>
    public static string ZipName(Selection selection, DateTime now)
    {
        if (selection is { Folders.Count: 1, Files.Count: 0 }
            && System.IO.Path.GetFileName(selection.Folders[0].TrimEnd('\\', '/')) is { Length: > 0 } name)
            return TransferNames.Clean(name + ".zip");
        return $"Homebase-{now:yyyyMMdd-HHmmss}.zip";
    }

    /// <summary>A new folder for one zip; <see cref="Discard"/> takes it away again.</summary>
    public static string NewWorkFolder()
    {
        var dir = System.IO.Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Discard(string? zipPath)
    {
        if (zipPath is null) return;
        try { Directory.Delete(System.IO.Path.GetDirectoryName(zipPath)!, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Zips left behind (the tray stopped while sending) go when it starts again.</summary>
    public static void CleanLeftovers()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes <paramref name="selection"/> into a new zip at <paramref name="zipPath"/>: each file at the top, each folder
    /// as a folder with everything under it. Hidden and system files, and folders that are links elsewhere (junctions),
    /// are left out; a file or folder that can't be read is skipped and counted. Stops before writing when the files add
    /// up to more than <paramref name="maxBytes"/>.
    /// </summary>
    public static Result Create(Selection selection, string zipPath, long maxBytes = TransferProtocol.MaxFileBytes, CancellationToken ct = default)
    {
        var entries = new List<(string Source, string Name)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var file in selection.Files)
            entries.Add((file, Unique(used, System.IO.Path.GetFileName(file))));
        foreach (var folder in selection.Folders)
        {
            var root = Unique(used, System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "folder");
            foreach (var file in Walk(folder, ref skipped))
                entries.Add((file, root + "/" + System.IO.Path.GetRelativePath(folder, file).Replace('\\', '/')));
        }
        if (entries.Count == 0) return new(null, skipped, "empty");

        long total = 0;
        foreach (var (source, _) in entries)
        {
            try { total += new FileInfo(source).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (total > maxBytes) return new(null, skipped, "too_large");

        var written = 0;
        try
        {
            using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var (source, name) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    FileStream input;
                    try { input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                        continue;
                    }
                    using (input)
                    {
                        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                        entry.LastWriteTime = ZipTime(File.GetLastWriteTime(source));
                        using var output = entry.Open();
                        input.CopyTo(output);
                    }
                    written++;
                }
            }
            if (written == 0)
            {
                File.Delete(zipPath);
                return new(null, skipped, "empty");
            }
            if (new FileInfo(zipPath).Length > maxBytes)
            {
                File.Delete(zipPath);
                return new(null, skipped, "too_large");
            }
            return new(zipPath, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TrayLog.Warn("Files", $"Zipping {entries.Count} file(s) failed: {ex.Message}");
            try { File.Delete(zipPath); } catch (Exception del) when (del is IOException or UnauthorizedAccessException) { }
            return new(null, skipped, "failed");
        }
    }

    /// <summary>
    /// Every file under <paramref name="folder"/>, without following junctions or taking hidden and system files. A folder
    /// that can't be listed (its permissions) is left out and counted in <paramref name="unreadable"/> (Codex review on #102).
    /// </summary>
    private static List<string> Walk(string folder, ref int unreadable)
    {
        var files = new List<string>();
        const FileAttributes Skip = FileAttributes.Hidden | FileAttributes.System;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
                continue;
            }
            foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((child.Attributes & Skip) != 0) continue;
                if (child is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub.FullName);
                }
                else
                {
                    files.Add(child.FullName);
                }
            }
        }
        return files;
    }

    private static string Unique(HashSet<string> used, string name)
    {
        if (used.Add(name)) return name;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var next = $"{stem} ({i}){ext}";
            if (used.Add(next)) return next;
        }
    }

    /// <summary>Zip keeps times from 1980 to 2107.</summary>
    private static DateTimeOffset ZipTime(DateTime t) =>
        t.Year < 1980 ? new DateTime(1980, 1, 1) : t.Year > 2107 ? new DateTime(2107, 12, 31) : t;
}
