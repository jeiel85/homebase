using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalOpsBot.Core.Updates;

public enum UpdateCheckErrorKind { Network, Timeout, RateLimit, ApiError, Integrity }

public sealed class UpdateCheckException : Exception
{
    public UpdateCheckErrorKind Kind { get; }
    public int? StatusCode { get; }
    public DateTimeOffset? RetryAtLocal { get; }

    public UpdateCheckException(UpdateCheckErrorKind kind, string message, int? statusCode = null, DateTimeOffset? retryAt = null)
        : base(message) { Kind = kind; StatusCode = statusCode; RetryAtLocal = retryAt; }
}

public sealed record UpdateInfo(
    Version Version,
    string DownloadUrl,
    string? Sha256Url,
    string ReleaseNotes,
    DateTimeOffset PublishedAt);

/// <summary>A downloaded package and the SHA-256 it was verified against (lowercase hex).</summary>
public sealed record DownloadedUpdate(string Path, string Sha256);

public sealed class UpdateService
{
    private static readonly Version CurrentVersion = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0);
    private const string RepoOwner = "jeiel85";
    private const string RepoName = "homebase";

    private readonly HttpClient _http;

    public UpdateService(HttpClient http) => _http = http;

    public string GetCurrentVersionString() => CurrentVersion.ToString(3);

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
        HttpResponseMessage response;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            response = await _http.GetAsync(url, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateCheckException(UpdateCheckErrorKind.Timeout, "GitHub API timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException(UpdateCheckErrorKind.Network, $"Network error: {ex.Message}");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Reset"))
        {
            var resetUnix = response.Headers.GetValues("X-RateLimit-Reset").FirstOrDefault();
            if (long.TryParse(resetUnix, out var unix))
            {
                var retryAt = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();
                throw new UpdateCheckException(UpdateCheckErrorKind.RateLimit, $"Rate limited until {retryAt:HH:mm}", retryAt: retryAt);
            }
        }

        if (!response.IsSuccessStatusCode)
            throw new UpdateCheckException(UpdateCheckErrorKind.ApiError, $"GitHub API returned {(int)response.StatusCode}", (int)response.StatusCode);

        var releases = await response.Content.ReadFromJsonAsync<GitHubRelease[]>(ct) ?? [];
        var latest = releases
            .Where(r => !r.Draft && !r.Prerelease)
            .Select(r =>
            {
                var tag = r.TagName ?? "";
                var verStr = Regex.Match(tag, @"\d+\.\d+\.\d+").Value;
                if (Version.TryParse(verStr, out var ver) && ver > CurrentVersion)
                {
                    // Target the combined installer package explicitly. Releases can carry
                    // several .zip assets, so matching "the first .zip" is order-dependent
                    // and unreliable — pin to the exact name the release workflow produces.
                    const string setupZipName = "Homebase-Setup.zip";
                    var setupAsset = r.Assets?.FirstOrDefault(a => string.Equals(a.Name, setupZipName, StringComparison.OrdinalIgnoreCase));
                    var shaAsset = r.Assets?.FirstOrDefault(a => string.Equals(a.Name, setupZipName + ".sha256", StringComparison.OrdinalIgnoreCase));
                    if (setupAsset?.BrowserDownloadUrl != null)
                        return new UpdateInfo(ver, setupAsset.BrowserDownloadUrl, shaAsset?.BrowserDownloadUrl, r.Body ?? "", r.PublishedAt);
                }
                return null;
            })
            .Where(u => u != null)
            .OrderByDescending(u => u!.Version)
            .FirstOrDefault();

        return latest;
    }

    public async Task<DownloadedUpdate> DownloadUpdateAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct)
    {
        // Fail closed before downloading anything: a package that cannot be verified is never installed.
        if (string.IsNullOrEmpty(info.Sha256Url))
            throw new UpdateCheckException(UpdateCheckErrorKind.Integrity,
                "The release publishes no checksum (Homebase-Setup.zip.sha256); refusing to install an unverified package.");

        var tempDir = Path.Combine(Path.GetTempPath(), "Homebase_Update");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"update_{Guid.NewGuid():N}.zip");

        // Download to disk (streamed), then release every handle before verifying so the
        // checksum read doesn't collide with the still-open write stream (FileShare.None).
        using (var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long readBytes = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                readBytes += bytesRead;
                if (totalBytes > 0)
                    progress?.Report((int)(readBytes * 100 / totalBytes));
            }
        }

        var sha256 = await VerifyChecksumAsync(tempFile, info.Sha256Url, ct);
        return new DownloadedUpdate(tempFile, sha256);
    }

    // Verifies the downloaded file against the release's published .sha256 asset and returns that
    // digest. Throws UpdateCheckException(Integrity) on a missing, malformed or mismatching checksum
    // and deletes the file.
    private async Task<string> VerifyChecksumAsync(string filePath, string sha256Url, CancellationToken ct)
    {
        string expected;
        try
        {
            var raw = await _http.GetStringAsync(sha256Url, ct);
            // The .sha256 asset is a lowercase hex digest, optionally "<hash>  <filename>".
            expected = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault() ?? "";
        }
        catch (HttpRequestException ex)
        {
            TryDelete(filePath);
            throw new UpdateCheckException(UpdateCheckErrorKind.Integrity, $"Could not fetch checksum: {ex.Message}");
        }
        if (!IsSha256Hex(expected))
        {
            TryDelete(filePath);
            throw new UpdateCheckException(UpdateCheckErrorKind.Integrity, "The published checksum is not a SHA-256 digest.");
        }

        string actual;
        await using (var stream = File.OpenRead(filePath))
        {
            var hash = await SHA256.HashDataAsync(stream, ct);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(filePath);
            throw new UpdateCheckException(UpdateCheckErrorKind.Integrity,
                $"Checksum mismatch — the download may be corrupt or tampered. Expected {expected}, got {actual}.");
        }
        return expected.ToLowerInvariant();
    }

    private static bool IsSha256Hex(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Applies a downloaded update package. Returns <c>true</c> once the elevated apply process is
    /// launched; <c>false</c> if the user declined the UAC prompt (nothing changed).
    /// </summary>
    /// <param name="update">The package and the SHA-256 it was verified against at download.</param>
    /// <param name="elevate">
    /// Request UAC elevation. The tray runs non-elevated, so it must pass <c>true</c> — the apply
    /// script writes to <c>C:\Program Files</c> and stops/starts the Windows service, all of which
    /// need admin. The Agent already runs as LocalSystem, so it leaves this <c>false</c>.
    /// </param>
    /// <remarks>
    /// The tray downloads into the user's %TEMP%, which every non-elevated process of that user can
    /// write. So nothing the elevated step runs or installs is read from there after it was checked:
    /// the script travels on the command line (<c>-EncodedCommand</c>, no .ps1 on disk), and it copies
    /// the zip into a folder only SYSTEM and Administrators can open, checks the SHA-256 again on that
    /// copy and extracts it there.
    /// </remarks>
    public bool ApplyUpdate(DownloadedUpdate update, bool elevate = false)
    {
        var psi = BuildApplyStartInfo(update, elevate);
        try
        {
            return Process.Start(psi) != null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED: the user dismissed the UAC prompt; nothing was changed.
            return false;
        }
    }

    /// <summary>Windows PowerShell by full path, so no folder on a user-writable PATH can stand in for it.</summary>
    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static ProcessStartInfo BuildApplyStartInfo(DownloadedUpdate update, bool elevate)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(BuildApplyScript(update)));
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}";
        return elevate
            ? new ProcessStartInfo(PowerShellPath, args)
            {
                // Non-elevated caller (the tray): request UAC so the script can write Program Files
                // and stop/start the service. Verb=runas requires UseShellExecute = true.
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }
            : new ProcessStartInfo(PowerShellPath, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
    }

    /// <summary>The PowerShell the apply step runs; pure, so tests pin its safety checks.</summary>
    public static string BuildApplyScript(DownloadedUpdate update)
    {
        if (!IsSha256Hex(update.Sha256))
            throw new ArgumentException("The verified SHA-256 is not a 64-digit hex digest.", nameof(update));
        if (update.Path.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Invalid package path.", nameof(update));

        // The update package is Homebase-Setup.zip, whose root contains Agent\ and Tray\ folders.
        // Extract, then copy each component into place so both the service and the tray app get
        // updated. A non-interpolated verbatim string + Replace keeps PowerShell's own braces as they
        // are; the path goes into a single-quoted literal, so its quotes are doubled. The script logs
        // to ProgramData\...\update.log and, on any failure, restarts the service so a botched update
        // never leaves the monitor permanently down.
        return @"$ErrorActionPreference = 'Stop'
$zip = '__ZIP_PATH__'
$expectedSha256 = '__SHA256__'
$work = Join-Path $env:ProgramData ('Homebase_Update_' + [Guid]::NewGuid().ToString('N'))
$extract = Join-Path $work 'package'
$agentDir = 'C:\Program Files\Homebase\Agent'
$trayDir = 'C:\Program Files\Homebase\Tray'
$serviceName = 'Homebase.Agent'
$logDir = Join-Path $env:ProgramData 'Homebase\logs'
$log = Join-Path $logDir 'update.log'

function Write-Log($m) {
    $line = ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $m)
    Write-Host $line
    try { Add-Content -Path $log -Value $line -ErrorAction SilentlyContinue } catch { }
}

# A folder only SYSTEM and Administrators can open, created with that ACL in one step so no
# non-elevated process can put or change anything in it.
function New-PrivateDirectory($path) {
    if (Test-Path -LiteralPath $path) { throw ('Work folder already exists: ' + $path) }
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier($sid)), 'FullControl', $inherit, 'None', 'Allow')))
    }
    [System.IO.Directory]::CreateDirectory($path, $acl) | Out-Null
}

# Copy with retries: even after the processes exit, a binary can stay briefly locked (AV scan,
# lingering handle). Retry a few times before giving up rather than failing the whole update.
function Copy-WithRetry($src, $dest) {
    for ($i = 0; $i -lt 5; $i++) {
        try {
            Copy-Item $src $dest -Recurse -Force -ErrorAction Stop
            return
        } catch {
            Write-Log ('Copy attempt ' + ($i + 1) + ' failed: ' + $_.Exception.Message)
            Start-Sleep -Seconds 2
        }
    }
    throw 'Failed to copy files after retries; a file may still be locked.'
}

try { New-Item -ItemType Directory -Force -Path $logDir | Out-Null } catch { }

try {
    New-PrivateDirectory $work
    $package = Join-Path $work 'Homebase-Setup.zip'
    Copy-Item -LiteralPath $zip -Destination $package -Force
    # Check the copy that gets installed, not the download the non-elevated side checked.
    $actualSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw ('Update package changed after it was verified (SHA-256 ' + $actualSha256 + '); aborting.')
    }

    Write-Log 'Extracting update package...'
    Expand-Archive -LiteralPath $package -DestinationPath $extract -Force

    $agentSrc = Join-Path $extract 'Agent'
    $traySrc  = Join-Path $extract 'Tray'
    if (-not (Test-Path (Join-Path $agentSrc 'Homebase.Agent.exe'))) {
        throw 'Agent binary missing from update package; aborting.'
    }

    Write-Log 'Stopping service...'
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue

    # Wait for the Agent process to actually exit so its files unlock (max ~30s).
    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Get-Process -Name 'Homebase.Agent' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
    Get-Process -Name 'Homebase.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 15; $i++) {
        if (-not (Get-Process -Name 'Homebase.Tray' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 400
    }

    Write-Log 'Updating Agent...'
    Copy-WithRetry (Join-Path $agentSrc '*') $agentDir

    if (Test-Path $traySrc) {
        Write-Log 'Updating Tray...'
        New-Item -ItemType Directory -Force -Path $trayDir | Out-Null
        Copy-WithRetry (Join-Path $traySrc '*') $trayDir
    }

    Write-Log 'Starting service...'
    Start-Service $serviceName

    # Relaunch the tray in the user's (non-elevated) session via explorer, so it comes back
    # after the update without waiting for the next logon. Only from an interactive session
    # (tray-initiated update); the LocalSystem /update path runs in session 0 with no desktop,
    # where the HKCU autostart brings it back on next logon instead. Best-effort either way.
    if ([Environment]::UserInteractive) {
        try {
            $trayExe = Join-Path $trayDir 'Homebase.Tray.exe'
            if (Test-Path $trayExe) { Start-Process 'explorer.exe' -ArgumentList $trayExe }
        } catch { Write-Log ('Tray relaunch note: ' + $_.Exception.Message) }
    }

    Write-Log 'Update complete.'
}
catch {
    Write-Log ('Update FAILED: ' + $_.Exception.Message)
    # Best-effort: keep the monitor alive even if the update failed.
    try { Start-Service $serviceName -ErrorAction SilentlyContinue } catch { }
    exit 1
}
finally {
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
".Replace("__ZIP_PATH__", update.Path.Replace("'", "''")).Replace("__SHA256__", update.Sha256.ToLowerInvariant());
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
