using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Core.Updates;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Shared read/write for the Agent's admin-owned config (ProgramData). Reads happen in-process; a
/// write edits the JSON in C# (System.Text.Json preserves arrays — avoiding PowerShell 5.1's
/// single-element-array unwrap) and applies it via one elevated write + Agent restart, since the
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
    /// Applies <paramref name="root"/> as the new config via an elevated write to ProgramData + an
    /// Agent restart. Returns <c>true</c> on success, <c>false</c> if the user declined the UAC prompt.
    /// </summary>
    /// <remarks>
    /// The config is staged in the user's %TEMP%, which every non-elevated process of that user can
    /// change — and the Agent runs as SYSTEM what the config names (cloudflared's path, say). So the
    /// elevated step reads the staged file once into memory, checks its SHA-256 against the one passed
    /// on its command line, and writes exactly those bytes; its script travels inline
    /// (<c>-EncodedCommand</c>), never as a .ps1 on disk.
    /// </remarks>
    public static async Task<bool> WriteAsync(JsonObject root)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var tempConfig = Path.Combine(Path.GetTempPath(), $"homebase_cfg_{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(tempConfig, bytes);

        try
        {
            var psi = new ProcessStartInfo(UpdateService.PowerShellPath,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildApplyScript(tempConfig, Sha256Hex(bytes)))))
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
            TryDelete(tempConfig);
        }
    }

    /// <summary>
    /// Sets <c><paramref name="section"/>.<paramref name="key"/></c> in <paramref name="root"/>, creating the
    /// section when the file has none (or it isn't an object). The Agent binds config without regard to
    /// case, and its JSON reader refuses two keys that differ only in case — so a file that spells it
    /// "LlmAdvisor"/"Model" is written under that spelling instead of gaining a second key that would stop
    /// the Agent from reading its config at all.
    /// </summary>
    internal static void SetValue(JsonObject root, string section, string key, JsonNode? value)
    {
        var sectionKey = KeyOf(root, section);
        if (root[sectionKey] is not JsonObject obj)
        {
            obj = new JsonObject();
            root[sectionKey] = obj;
        }
        obj[KeyOf(obj, key)] = value;
    }

    private static string KeyOf(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// The elevated helper: read the staged file once, check it is what the Tray wrote, and write those
    /// bytes as a new file (owned by Administrators, inheriting the folder's ACL — so a config a
    /// non-admin created earlier, and still owns, is replaced rather than written into); then restart
    /// the Agent so it re-reads at startup. No JSON parsing in PowerShell.
    /// </summary>
    internal static string BuildApplyScript(string stagedPath, string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Not a SHA-256 hex digest.", nameof(sha256));
        if (stagedPath.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Invalid path.", nameof(stagedPath));
        return @"$ErrorActionPreference = 'Stop'
$src = '__SRC__'
$expected = '__SHA256__'
$configDir = 'C:\ProgramData\Homebase\config'
$configFile = Join-Path $configDir 'appsettings.json'
$bytes = [System.IO.File]::ReadAllBytes($src)
$sha = [System.Security.Cryptography.SHA256]::Create()
$actual = ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
if ($actual -ne $expected) { throw 'The staged configuration changed after Homebase wrote it; nothing was applied.' }
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$staged = Join-Path $configDir ('appsettings.' + [Guid]::NewGuid().ToString('N') + '.new')
$out = [System.IO.File]::Open($staged, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try { $out.Write($bytes, 0, $bytes.Length) } finally { $out.Dispose() }
if (Test-Path -LiteralPath $configFile) { Remove-Item -LiteralPath $configFile -Force }
Move-Item -LiteralPath $staged -Destination $configFile
# Restart is best-effort: the config is already written and the service reads it at startup.
try {
    $svc = Get-Service -Name 'Homebase.Agent' -ErrorAction SilentlyContinue
    if ($svc) { Restart-Service -Name 'Homebase.Agent' -Force -ErrorAction SilentlyContinue }
} catch { }
".Replace("__SRC__", stagedPath.Replace("'", "''")).Replace("__SHA256__", sha256.ToLowerInvariant());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
