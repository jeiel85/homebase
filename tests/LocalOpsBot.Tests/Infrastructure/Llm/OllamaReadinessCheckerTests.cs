using System.Net;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure.Llm;

public sealed class OllamaReadinessCheckerTests
{
    private const string Endpoint = "http://127.0.0.1:11434";

    private static OllamaReadinessChecker Checker(HttpMessageHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task Ready_when_server_up_and_model_present()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{"models":[{"name":"llama3.2:1b"},{"name":"qwen2:0.5b"}]}""");

        var result = await Checker(handler).CheckAsync(Endpoint, "llama3.2:1b", CancellationToken.None);

        Assert.Equal(OllamaReadiness.Ready, result.Status);
    }

    [Fact]
    public async Task ModelMissing_when_model_not_installed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{"models":[{"name":"qwen2:0.5b"}]}""");

        var result = await Checker(handler).CheckAsync(Endpoint, "llama3.2:1b", CancellationToken.None);

        Assert.Equal(OllamaReadiness.ModelMissing, result.Status);
        Assert.Contains("qwen2:0.5b", result.InstalledModels);
    }

    [Fact]
    public async Task Bare_model_name_matches_any_tag()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{"models":[{"name":"llama3.2:latest"}]}""");

        var result = await Checker(handler).CheckAsync(Endpoint, "llama3.2", CancellationToken.None);

        Assert.Equal(OllamaReadiness.Ready, result.Status);
    }

    [Fact]
    public async Task Unreachable_on_error_status()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.InternalServerError, "boom");

        var result = await Checker(handler).CheckAsync(Endpoint, "llama3.2:1b", CancellationToken.None);

        Assert.Equal(OllamaReadiness.Unreachable, result.Status);
    }

    [Fact]
    public async Task Unreachable_on_connection_exception()
    {
        var result = await Checker(new ThrowingHandler()).CheckAsync(Endpoint, "llama3.2:1b", CancellationToken.None);

        Assert.Equal(OllamaReadiness.Unreachable, result.Status);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
