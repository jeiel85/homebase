using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using Microsoft.Win32.SafeHandles;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Turns phone approvals for Claude Code on and off (DECISIONS M-48): our PermissionRequest hook in
/// the user's <c>~/.claude/settings.json</c> (everything else in that file is kept, and the previous
/// file is saved next to it as <c>settings.json.homebase.bak</c>) and the Tray's loopback listener it
/// calls. Off by default; only the user's own toggle in Settings › Phone changes it. On start the
/// Tray resumes the listener when the hook is installed, and rewrites the hook if the port moved.
/// </summary>
internal static class ClaudeHookSetup
{
    private sealed record Saved(int Port, string Token);

    private static readonly object Gate = new();
    private static ClaudeHookServer? _server;

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static string StatePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "claude-hook.json");

    /// <summary>Whether our hook is in settings.json.</summary>
    public static bool IsInstalled() => ClaudeHook.InstalledCommand(ReadSettings()) is not null;

    /// <summary>Installs the hook and starts the listener. Returns an error message, or null.</summary>
    public static string? Enable()
    {
        try
        {
            lock (Gate)
            {
                var server = EnsureServer();
                WriteSettings(ClaudeHook.ApplyToSettings(ReadSettings(), ClaudeHook.Command(server.Port, LoadState()!.Token)));
            }
            TrayLog.Info("claude-hook", "Phone approvals for Claude Code turned on.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            TrayLog.Warn("claude-hook", "Turning phone approvals on failed.", ex);
            return ex.Message;
        }
    }

    /// <summary>Removes the hook and stops the listener. Returns an error message, or null.</summary>
    public static string? Disable()
    {
        try
        {
            lock (Gate)
            {
                var text = ReadSettings();
                if (ClaudeHook.InstalledCommand(text) is not null) WriteSettings(ClaudeHook.ApplyToSettings(text, null));
                _server?.Dispose();
                _server = null;
            }
            TrayLog.Info("claude-hook", "Phone approvals for Claude Code turned off.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            TrayLog.Warn("claude-hook", "Turning phone approvals off failed.", ex);
            return ex.Message;
        }
    }

    /// <summary>
    /// Uninstall (<c>Homebase.Tray.exe --remove-claude-hook &lt;settings.json&gt;</c>, run by
    /// uninstall-service.ps1 for each user profile): takes our hook out of that file and leaves the
    /// rest. Runs elevated during uninstall, so it rejects reparse points (junctions/symlinks) and
    /// hardlinks, verifies the opened handle's final path matches <paramref name="settingsPath"/>,
    /// and updates that handle in place without creating auxiliary files in the user's folder.
    /// </summary>
    public static bool RemoveFrom(string settingsPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(settingsPath);
            if (!File.Exists(fullPath)) return true;
            if (HasReparsePointOnChain(fullPath))
            {
                TrayLog.Warn("claude-hook", $"Skipped {fullPath}: path contains a symlink or junction.");
                return false;
            }
            using var handle = OpenWithoutFollowingReparse(fullPath, FileAccess.ReadWrite);
            if (handle is null)
            {
                TrayLog.Warn("claude-hook", $"Skipped {fullPath}: could not open safely without following links.");
                return false;
            }
            if (!IsSafeSingleLinkTarget(handle, fullPath))
            {
                TrayLog.Warn("claude-hook", $"Skipped {fullPath}: file is a reparse point, hardlink, or resolved outside expected path.");
                return false;
            }
            using var stream = new FileStream(handle, FileAccess.ReadWrite);
            string text;
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
                text = reader.ReadToEnd();
            if (ClaudeHook.InstalledCommand(text) is null) return true;
            var updated = ClaudeHook.ApplyToSettings(text, null);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(updated);
            stream.SetLength(0);
            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            TrayLog.Info("claude-hook", $"Phone approval hook removed from {fullPath} (uninstall).");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            TrayLog.Warn("claude-hook", $"Removing the phone approval hook from {settingsPath} failed.", ex);
            return false;
        }
    }

    internal static bool HasReparsePointOnChain(string fullPath)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return true;
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return false;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    private static SafeFileHandle? OpenWithoutFollowingReparse(string fullPath, FileAccess access)
    {
        var desired = access == FileAccess.Read ? GenericRead : GenericRead | GenericWrite;
        var handle = CreateFileW(fullPath, desired, 0, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    private static bool IsSafeSingleLinkTarget(SafeFileHandle handle, string expectedFullPath)
    {
        if (!GetFileInformationByHandle(handle, out var info)) return false;
        if ((info.FileAttributes & FileAttributeReparsePoint) != 0) return false;
        if (info.NumberOfLinks != 1) return false;

        var buf = new char[1024];
        var len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, 0);
        if (len == 0 || len >= buf.Length) return false;
        var finalPath = new string(buf, 0, (int)len);
        if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
        return string.Equals(finalPath, expectedFullPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>At Tray start: when the hook is installed, listen again (and fix the hook if the port changed).</summary>
    public static void Resume()
    {
        try
        {
            lock (Gate)
            {
                var text = ReadSettings();
                var installed = ClaudeHook.InstalledCommand(text);
                if (installed is null) return;
                var server = EnsureServer();
                var wanted = ClaudeHook.Command(server.Port, LoadState()!.Token);
                if (installed != wanted) WriteSettings(ClaudeHook.ApplyToSettings(text, wanted));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            TrayLog.Warn("claude-hook", "Resuming phone approvals failed; Claude Code asks on the PC.", ex);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _server?.Dispose();
            _server = null;
        }
    }

    private static ClaudeHookServer EnsureServer()
    {
        if (_server is not null) return _server;
        var state = LoadState() ?? new Saved(ClaudeHookServer.DefaultPort, ClaudeHookServer.NewToken());
        var server = ClaudeHookServer.Start(state.Port, state.Token, () => MobileHost.Client);
        if (server.Port != state.Port || LoadState() is null) SaveState(state with { Port = server.Port });
        _server = server;
        return server;
    }

    private static Saved? LoadState()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(StatePath));
            return s is { Port: > 0 and < 65536, Token.Length: >= 32 } ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void SaveState(Saved state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }

    private static string? ReadSettings() => File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;

    private static void WriteSettings(string json) => WriteSettings(SettingsPath, json);

    /// <summary>Atomic replace, keeping the previous file as <c>settings.json.homebase.bak</c>.</summary>
    private static void WriteSettings(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".homebase.bak", overwrite: true);
        var tmp = path + ".homebase.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
