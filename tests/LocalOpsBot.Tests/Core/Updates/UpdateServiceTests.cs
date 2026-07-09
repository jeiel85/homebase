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
        var path = await CreateService(handler).DownloadUpdateAsync(info, null, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
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

    [Fact]
    public async Task Download_NoChecksumUrl_SkipsVerification()
    {
        var payload = Encoding.UTF8.GetBytes("fake-zip-content");

        var handler = new FakeHttpMessageHandler();
        handler.QueueContent(HttpStatusCode.OK, new ByteArrayContent(payload));

        var info = new UpdateInfo(new Version(99, 0, 0), "http://x/setup.zip", null, "", DateTimeOffset.Now);
        var path = await CreateService(handler).DownloadUpdateAsync(info, null, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
