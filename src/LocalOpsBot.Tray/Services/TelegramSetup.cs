using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LocalOpsBot.Tray.Services;

internal enum TelegramApplyOutcome { Saved, InvalidToken, InvalidChatId, ScriptMissing, HelperFailed, Failed, Cancelled }

internal sealed record TelegramApplyResult(TelegramApplyOutcome Outcome, int ExitCode = 0, string? Error = null);

/// <summary>
/// Saves the bot token and chat ID through the installer's <c>configure-telegram.ps1</c>, the one
/// privileged step: it sets the machine token, writes the chat allowlist and restarts the
/// service, behind a single UAC prompt. Inputs are format-validated first so they carry no shell
/// metacharacters.
/// </summary>
internal static class TelegramSetup
{
    // Same formats the installer validates: token "digits:base64ish", chat ID signed integer.
    public static bool IsValidToken(string token) => Regex.IsMatch(token, @"^\d{6,10}:[A-Za-z0-9_-]{30,}$");
    public static bool IsValidChatId(string chatId) => Regex.IsMatch(chatId, @"^-?\d{4,}$");

    public static async Task<TelegramApplyResult> ApplyAsync(string token, string chatId)
    {
        if (!IsValidToken(token)) return new(TelegramApplyOutcome.InvalidToken);
        if (!IsValidChatId(chatId)) return new(TelegramApplyOutcome.InvalidChatId);

        var script = ResolveConfigureScriptPath();
        if (script is null) return new(TelegramApplyOutcome.ScriptMissing);

        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
                $"-File \"{script}\" -Token \"{token}\" -ChatId \"{chatId}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new(TelegramApplyOutcome.HelperFailed);

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0
                ? new(TelegramApplyOutcome.Saved)
                : new(TelegramApplyOutcome.Failed, proc.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(TelegramApplyOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            return new(TelegramApplyOutcome.Failed, Error: ex.Message);
        }
    }

    // The Tray runs from {app}\Tray; configure-telegram.ps1 is installed one level up at {app}.
    private static string? ResolveConfigureScriptPath()
    {
        var trayDir = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var appDir = System.IO.Directory.GetParent(trayDir)?.FullName;
        if (appDir is null) return null;
        var path = System.IO.Path.Combine(appDir, "configure-telegram.ps1");
        return System.IO.File.Exists(path) ? path : null;
    }
}
