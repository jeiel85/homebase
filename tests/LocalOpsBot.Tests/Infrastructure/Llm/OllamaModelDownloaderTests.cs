using System.Net;
using System.Text.Json;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure.Llm;

public sealed class OllamaModelDownloaderTests
{
    private const string Endpoint = "http://127.0.0.1:11434";

    [Fact]
    public async Task Streams_progress_and_requires_an_explicit_success()
    {
        var handler = new CaptureRequest(
            "{\"status\":\"pulling manifest\"}\n{\"status\":\"pulling layer\",\"completed\":50,\"total\":100}\n{\"status\":\"success\"}\n");
        var progress = new List<OllamaPullProgress>();
        await new OllamaModelDownloader(new HttpClient(handler)).PullAsync(
            Endpoint + "/", "qwen3.5:4b", new Capture(progress), CancellationToken.None);

        Assert.Equal("/api/pull", handler.Uri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("qwen3.5:4b", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(new OllamaPullProgress("pulling layer", 50, 100), progress[1]);
        Assert.Equal("success", progress.Last().Status);
    }

    [Fact]
    public async Task An_interrupted_stream_is_not_reported_as_success()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, "{\"status\":\"pulling layer\",\"completed\":10,\"total\":100}\n");
        await Assert.ThrowsAsync<IOException>(() => new OllamaModelDownloader(new HttpClient(handler))
            .PullAsync(Endpoint, "qwen3.5:4b", null, CancellationToken.None));
    }

    [Fact]
    public async Task An_ollama_error_is_reported_and_a_retry_can_succeed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, "{\"error\":\"model not found\"}\n");
        handler.QueueResponse(HttpStatusCode.OK, "{\"status\":\"success\"}\n");
        var downloader = new OllamaModelDownloader(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.PullAsync(Endpoint, "missing:1b", null, CancellationToken.None));
        Assert.Contains("model not found", error.Message);
        await downloader.PullAsync(Endpoint, "qwen3.5:4b", null, CancellationToken.None);
    }

    [Fact]
    public async Task An_http_error_is_not_reported_as_success()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable, "unavailable");
        await Assert.ThrowsAsync<HttpRequestException>(() => new OllamaModelDownloader(new HttpClient(handler))
            .PullAsync(Endpoint, "qwen3.5:4b", null, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, "{\"status\":\"success\"}\n");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OllamaModelDownloader(new HttpClient(handler))
            .PullAsync(Endpoint, "qwen3.5:4b", null, new CancellationToken(true)));
    }

    [Fact]
    public async Task Cancellation_interrupts_an_active_download_stream()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new WaitingStream();
        var handler = new FakeHttpMessageHandler();
        handler.QueueContent(HttpStatusCode.OK, new StreamContent(stream));
        var download = new OllamaModelDownloader(new HttpClient(handler))
            .PullAsync(Endpoint, "qwen3.5:4b", null, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Capture(List<OllamaPullProgress> items) : IProgress<OllamaPullProgress>
    {
        public void Report(OllamaPullProgress value) => items.Add(value);
    }

    private sealed class CaptureRequest(string stream) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            Uri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream) };
        }
    }

    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
