using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// One-click local-AI setup. Finds an installed Ollama, registers it to run at login (the piece
/// that was missing — an installed-but-unregistered Ollama silently isn't running after a reboot,
/// which is why the readiness probe reported "not detected"), and starts it if it isn't already.
/// All best-effort and non-elevated (writes only HKCU).
/// </summary>
internal static class OllamaSetup
{
    private static string? FindApp()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(local, "Programs", "Ollama", "ollama app.exe"), // GUI + tray + server
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"),     // CLI (serve) fallback
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsInstalled() => FindApp() != null;

    /// <summary>
    /// A model name built only from the characters Ollama uses (<c>llama3.2:1b</c>, <c>user/model:tag</c>).
    /// Shared by the model download input and the Agent config writer.
    /// </summary>
    public static bool IsValidModelName(string? model) =>
        !string.IsNullOrEmpty(model)
        && model.Length <= 200
        && char.IsAsciiLetterOrDigit(model[0])
        && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/');

    public static bool IsRunning()
    {
        try
        {
            return Process.GetProcessesByName("ollama").Length > 0
                || Process.GetProcessesByName("ollama app").Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Registers Ollama to run at login and starts it if it isn't running. Returns true when Ollama
    /// is installed (so the caller can re-probe); false if it isn't installed at all.
    /// </summary>
    public static bool StartAndEnableAutostart()
    {
        var app = FindApp();
        if (app == null) return false;

        // Autostart via HKCU Run — this is what was missing, so Ollama comes back after a reboot.
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.SetValue("Ollama", $"\"{app}\"");
        }
        catch { /* best-effort */ }

        if (!IsRunning())
        {
            try { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true }); }
            catch { /* best-effort */ }
        }
        return true;
    }
}
