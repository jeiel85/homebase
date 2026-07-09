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

    public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LocalOpsBot_Update");
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

        await VerifyChecksumAsync(tempFile, info.Sha256Url, ct);
        return tempFile;
    }

    // Verifies the downloaded file against the release's published .sha256 asset.
    // Throws UpdateCheckException(Integrity) on mismatch and deletes the bad file.
    private async Task VerifyChecksumAsync(string filePath, string? sha256Url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sha256Url))
            return; // No checksum published — nothing to verify against.

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
            throw new UpdateCheckException(UpdateCheckErrorKind.Integrity, $"Could not fetch checksum: {ex.Message}");
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
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    public void ApplyUpdate(string zipPath)
    {
        // The update package is Homebase-Setup.zip, whose root contains Agent\ and
        // Tray\ folders. Extract to a temp dir, then copy each component into place so
        // both the service and the tray app get updated. Uses a non-interpolated verbatim
        // string + Replace so PowerShell's own braces don't need doubling. The script
        // logs to ProgramData\...\update.log and, on any failure, restarts the service so
        // a botched update never leaves the monitor permanently down.
        var script = @"$ErrorActionPreference = 'Stop'
$zip = '__ZIP_PATH__'
$extract = Join-Path ([System.IO.Path]::GetTempPath()) ('LocalOpsBot_Update_' + [Guid]::NewGuid().ToString('N'))
$agentDir = 'C:\Program Files\LocalOpsBot\Agent'
$trayDir = 'C:\Program Files\LocalOpsBot\Tray'
$serviceName = 'LocalOpsBot.Agent'
$logDir = Join-Path $env:ProgramData 'LocalOpsBot\logs'
$log = Join-Path $logDir 'update.log'

function Write-Log($m) {
    $line = ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $m)
    Write-Host $line
    try { Add-Content -Path $log -Value $line -ErrorAction SilentlyContinue } catch { }
}

try { New-Item -ItemType Directory -Force -Path $logDir | Out-Null } catch { }

try {
    Write-Log 'Extracting update package...'
    Expand-Archive -Path $zip -DestinationPath $extract -Force

    $agentSrc = Join-Path $extract 'Agent'
    $traySrc  = Join-Path $extract 'Tray'
    if (-not (Test-Path (Join-Path $agentSrc 'LocalOpsBot.Agent.exe'))) {
        throw 'Agent binary missing from update package; aborting.'
    }

    Write-Log 'Stopping service...'
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue

    # Wait for the Agent process to actually exit so its files unlock (max ~30s).
    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Get-Process -Name 'LocalOpsBot.Agent' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
    Get-Process -Name 'LocalOpsBot.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Log 'Updating Agent...'
    Copy-Item (Join-Path $agentSrc '*') $agentDir -Recurse -Force

    if (Test-Path $traySrc) {
        Write-Log 'Updating Tray...'
        New-Item -ItemType Directory -Force -Path $trayDir | Out-Null
        Copy-Item (Join-Path $traySrc '*') $trayDir -Recurse -Force
    }

    Write-Log 'Starting service...'
    Start-Service $serviceName
    Write-Log 'Update complete.'
}
catch {
    Write-Log ('Update FAILED: ' + $_.Exception.Message)
    # Best-effort: keep the monitor alive even if the update failed.
    try { Start-Service $serviceName -ErrorAction SilentlyContinue } catch { }
    exit 1
}
finally {
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
}
".Replace("__ZIP_PATH__", zipPath);

        var psFile = Path.Combine(Path.GetTempPath(), $"localops_update_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(psFile, script);

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{psFile}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(psi);
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
