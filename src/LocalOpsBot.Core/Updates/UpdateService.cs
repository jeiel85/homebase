using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalOpsBot.Core.Updates;

public enum UpdateCheckErrorKind { Network, Timeout, RateLimit, ApiError }

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
    private const string RepoOwner = "anomalyco";
    private const string RepoName = "localops_bot";

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
                    var exeAsset = r.Assets?.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    var shaAsset = r.Assets?.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
                    if (exeAsset?.BrowserDownloadUrl != null)
                        return new UpdateInfo(ver, exeAsset.BrowserDownloadUrl, shaAsset?.BrowserDownloadUrl, r.Body ?? "", r.PublishedAt);
                }
                return null;
            })
            .Where(u => u != null)
            .OrderByDescending(u => u!.Version)
            .FirstOrDefault();

        return latest;
    }

    public async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<int>? progress, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LocalOpsBot_Update");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"update_{Guid.NewGuid():N}.zip");

        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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

        return tempFile;
    }

    public void ApplyUpdate(string zipPath)
    {
        var script = $@"$zip = '{zipPath}'
$agentDir = 'C:\Program Files\LocalOpsBot\Agent'
$trayDir = 'C:\Program Files\LocalOpsBot\Tray'
$serviceName = 'LocalOpsBot.Agent'

Write-Host 'Stopping service...'
Stop-Service $serviceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

Write-Host 'Extracting update...'
Expand-Archive -Path $zip -DestinationPath $agentDir -Force

$trayZip = Join-Path $agentDir 'LocalOpsBot.Tray.zip'
if (Test-Path $trayZip) {{
    Expand-Archive -Path $trayZip -DestinationPath $trayDir -Force
    Remove-Item $trayZip -Force
}}

Write-Host 'Starting service...'
Start-Service $serviceName

Write-Host 'Cleaning up...'
Remove-Item $zip -Force

Write-Host 'Update complete.'
";

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
