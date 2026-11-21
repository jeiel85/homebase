using System.Net;
using System.Security.Cryptography;
using System.Text;
using LocalOpsBot.Core.Updates;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Core.Updates;

public sealed class UpdateServiceTests
{
    private static UpdateService CreateService(FakeHttpMessageHandler handler)
        => new(new HttpClient(handler));

    private static string ReleasesJson(string tag, params (string name, string url)[] assets)
    {
        var assetJson = string.Join(",", assets.Select(a =>
            $"{{\"name\":\"{a.name}\",\"browser_download_url\":\"{a.url}\"}}"));
        return $"[{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":false," +
               $"\"body\":\"notes\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[{assetJson}]}}]";
    }

    [Fact]
    public async Task CheckForUpdate_PicksSetupZip_EvenWhenOtherZipsPresent()
    {
        // Agent.zip is listed first — the old "first .zip wins" logic would have grabbed
        // the wrong asset. The fix must pin to Homebase-Setup.zip regardless of order.
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, ReleasesJson("v99.0.0",
            ("LocalOpsBot-Agent.zip", "http://x/agent.zip"),
            ("Homebase-Setup.zip", "http://x/setup.zip"),
            ("Homebase-Setup.zip.sha256", "http://x/setup.sha")));

        var info = await CreateService(handler).CheckForUpdateAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("http://x/setup.zip", info!.DownloadUrl);
        Assert.Equal("http://x/setup.sha", info.Sha256Url);
    }

    [Fact]
    public async Task Download_ValidChecksum_ReturnsExistingFile()
    {
        var payload = Encoding.UTF8.GetBytes("fake-zip-content");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var handler = new FakeHttpMessageHandler();
        handler.QueueContent(HttpStatusCode.OK, new ByteArrayContent(payload));
        handler.QueueContent(HttpStatusCode.OK, new StringContent(hash));

        var info = new UpdateInfo(new Version(99, 0, 0), "http://x/setup.zip", "http://x/setup.sha", "", DateTimeOffset.Now);
        var (path, sha256) = await CreateService(handler).DownloadUpdateAsync(info, null, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(hash, sha256);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_BadChecksum_ThrowsIntegrity_AndDeletesFile()
    {
        var payload = Encoding.UTF8.GetBytes("fake-zip-content");

        var handler = new FakeHttpMessageHandler();
        handler.QueueContent(HttpStatusCode.OK, new ByteArrayContent(payload));
        handler.QueueContent(HttpStatusCode.OK, new StringContent("deadbeefnotarealhash"));

        var info = new UpdateInfo(new Version(99, 0, 0), "http://x/setup.zip", "http://x/setup.sha", "", DateTimeOffset.Now);

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() =>
            CreateService(handler).DownloadUpdateAsync(info, null, CancellationToken.None));

        Assert.Equal(UpdateCheckErrorKind.Integrity, ex.Kind);
    }

    // An unverifiable package is never installed. This used to skip verification and install it.
    [Fact]
    public async Task Download_NoChecksumUrl_ThrowsIntegrity_WithoutDownloading()
    {
        var handler = new FakeHttpMessageHandler();
        var info = new UpdateInfo(new Version(99, 0, 0), "http://x/setup.zip", null, "", DateTimeOffset.Now);

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() =>
            CreateService(handler).DownloadUpdateAsync(info, null, CancellationToken.None));

        Assert.Equal(UpdateCheckErrorKind.Integrity, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_ChecksumNotADigest_ThrowsIntegrity()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueContent(HttpStatusCode.OK, new ByteArrayContent(Encoding.UTF8.GetBytes("fake-zip-content")));
        handler.QueueContent(HttpStatusCode.OK, new StringContent("<html>not found</html>"));
        var info = new UpdateInfo(new Version(99, 0, 0), "http://x/setup.zip", "http://x/setup.sha", "", DateTimeOffset.Now);

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() =>
            CreateService(handler).DownloadUpdateAsync(info, null, CancellationToken.None));

        Assert.Equal(UpdateCheckErrorKind.Integrity, ex.Kind);
    }

    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ApplyScript_RechecksTheHashOnAPrivateCopy_BeforeExtracting()
    {
        var script = UpdateService.BuildApplyScript(
            new DownloadedUpdate(@"C:\Users\u\AppData\Local\Temp\a.zip", Digest.ToUpperInvariant()));

        Assert.Contains($"$expectedSha256 = '{Digest}'", script);
        var privateDir = script.IndexOf("New-PrivateDirectory $work", StringComparison.Ordinal);
        var copy = script.IndexOf("Copy-Item -LiteralPath $zip -Destination $package", StringComparison.Ordinal);
        var hashCheck = script.IndexOf("Get-FileHash -LiteralPath $package", StringComparison.Ordinal);
        var extract = script.IndexOf("Expand-Archive -LiteralPath $package", StringComparison.Ordinal);
        Assert.True(privateDir > 0 && privateDir < copy && copy < hashCheck && hashCheck < extract);
        // Nothing is extracted into, or run from, the user-writable %TEMP%.
        Assert.DoesNotContain("GetTempPath", script);
    }

    [Fact]
    public void ApplyScript_QuotesThePathAsALiteral()
    {
        var script = UpdateService.BuildApplyScript(new DownloadedUpdate(@"C:\Users\O'Brien\x'; calc; '.zip", Digest));

        Assert.Contains(@"$zip = 'C:\Users\O''Brien\x''; calc; ''.zip'", script);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Homebase\Agent\Homebase.Agent.exe""", @"C:\Program Files\Homebase")]
    [InlineData(@"D:\Apps\Homebase\Agent\Homebase.Agent.exe", @"D:\Apps\Homebase")]
    [InlineData(@"""D:\My Apps\HB\agent\Homebase.Agent.exe"" --run", @"D:\My Apps\HB")]
    [InlineData(@"C:\Other\thing.exe", null)]
    public void ApplyScript_UpdatesTheFolderTheServiceRunsFrom(string binPath, string? root)
    {
        var script = UpdateService.BuildApplyScript(new DownloadedUpdate(@"C:\a.zip", Digest));

        // Setup lets the user pick the install folder (#80): Agent and Tray go where the service runs from.
        Assert.Contains("$installRoot = 'C:\\Program Files\\Homebase'", script); // when the service can't be read
        Assert.Contains("$agentDir = Join-Path $installRoot 'Agent'", script);
        Assert.Contains("$trayDir = Join-Path $installRoot 'Tray'", script);
        Assert.DoesNotContain(@"'C:\Program Files\Homebase\Agent'", script);
        // The script's own pattern, as PowerShell's -match runs it (case-insensitive .NET regex).
        var start = script.IndexOf("-match '", StringComparison.Ordinal) + "-match '".Length;
        var pattern = script[start..script.IndexOf('\'', start)];
        var m = System.Text.RegularExpressions.Regex.Match(binPath, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.Equal(root, m.Success ? m.Groups[1].Value : null);
    }

    [Theory]
    [InlineData(@"C:\a.zip", "nothex")]
    [InlineData(@"C:\a.zip", "0123")]
    [InlineData("C:\\a\r\n.zip", Digest)]
    public void ApplyScript_RejectsBadInput(string path, string sha256) =>
        Assert.Throws<ArgumentException>(() => UpdateService.BuildApplyScript(new DownloadedUpdate(path, sha256)));

    [Fact]
    public void ApplyStartInfo_RunsSystemPowerShellWithTheScriptInline()
    {
        var psi = UpdateService.BuildApplyStartInfo(new DownloadedUpdate(@"C:\a.zip", Digest), elevate: true);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), psi.FileName);
        Assert.Equal("runas", psi.Verb);
        Assert.Contains("-NoProfile", psi.Arguments);
        Assert.Contains("-EncodedCommand ", psi.Arguments);
        Assert.DoesNotContain("-File", psi.Arguments);
    }
}
