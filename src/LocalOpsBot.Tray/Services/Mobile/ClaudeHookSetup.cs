using System.IO;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

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

    /// <summary>Atomic replace, keeping the previous file as <c>settings.json.homebase.bak</c>.</summary>
    private static void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".homebase.bak", overwrite: true);
        var tmp = SettingsPath + ".homebase.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
