using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Shared read/write for the Agent's admin-owned config (ProgramData). Reads happen in-process; a
/// write edits the JSON in C# (System.Text.Json preserves arrays — avoiding PowerShell 5.1's
/// single-element-array unwrap) and applies it via one elevated copy + Agent restart, since the
/// config is admin-owned and the Agent reads it at startup.
/// </summary>
internal static class ElevatedConfig
{
    /// <summary>
    /// The current config as a <see cref="JsonObject"/> (empty when absent). Throws when the file
    /// exists but isn't valid JSON, so a subsequent write never clobbers an unreadable config.
    /// </summary>
    public static async Task<JsonObject> ReadAsync()
    {
        if (!File.Exists(TrayConfig.ConfigPath))
            return new JsonObject();
        var text = await File.ReadAllTextAsync(TrayConfig.ConfigPath);
        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidOperationException("The configuration file is not a JSON object.");
    }

    /// <summary>
    /// Applies <paramref name="root"/> as the new config via an elevated copy to ProgramData + an
    /// Agent restart. Returns <c>true</c> on success, <c>false</c> if the user declined the UAC prompt.
    /// </summary>
    public static async Task<bool> WriteAsync(JsonObject root)
    {
        var tempConfig = Path.Combine(Path.GetTempPath(), $"homebase_cfg_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempConfig,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Elevated helper: copy the staged file over the admin-owned config, then restart the Agent
        // so it re-reads at startup. No JSON parsing in PowerShell — copy only.
        var script = @"$ErrorActionPreference = 'Stop'
$src = '__SRC__'
$configDir = 'C:\ProgramData\Homebase\config'
$configFile = Join-Path $configDir 'appsettings.json'
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
Copy-Item -LiteralPath $src -Destination $configFile -Force
# Restart is best-effort: the config is already written and the service reads it at startup.
try {
    $svc = Get-Service -Name 'Homebase.Agent' -ErrorAction SilentlyContinue
    if ($svc) { Restart-Service -Name 'Homebase.Agent' -Force -ErrorAction SilentlyContinue }
} catch { }
".Replace("__SRC__", tempConfig.Replace("'", "''"));

        var psFile = Path.Combine(Path.GetTempPath(), $"homebase_cfgapply_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(psFile, script);

        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{psFile}\"")
            {
                UseShellExecute = true, // required for Verb=runas
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // user dismissed the UAC prompt — nothing changed
        }
        finally
        {
            TryDelete(psFile);
            TryDelete(tempConfig);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
