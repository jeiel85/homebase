using System.Net;
using System.Text.Json;
using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure.Llm;

public sealed class OllamaLlmClientTests
{
    private static LlmAdvisorOptions Opts() =>
        new() { Endpoint = "http://127.0.0.1:11434", Model = "llama3.2:1b", TimeoutSeconds = 5 };

    private static OllamaLlmClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Opts());

    [Fact]
    public async Task Generate_parses_response_field()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{"response":"Check your CPU.","done":true}""");

        var result = await Client(handler).GenerateAsync("hi", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Check your CPU.", result.Text);
    }

    [Fact]
    public async Task Generate_sends_keep_alive_and_num_predict_for_speed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{"response":"ok","done":true}""");
        var client = new OllamaLlmClient(new HttpClient(handler),
            new LlmAdvisorOptions { MaxTokens = 200, KeepAliveMinutes = 5 });

        await client.GenerateAsync("hi", CancellationToken.None);

        var body = await handler.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("\"keep_alive\":\"5m\"", body);
        Assert.Contains("\"num_predict\":200", body);
    }

    [Fact]
    public async Task Generate_disables_thinking_so_the_answer_keeps_its_token_budget()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK,
            """{"response":"메모리를 많이 쓰는 앱을 확인하세요.","thinking":"","done":true}""");
        var client = new OllamaLlmClient(new HttpClient(handler),
            new LlmAdvisorOptions { Model = "qwen3.5:4b", MaxTokens = 350 });

        var result = await client.GenerateAsync("Give brief PC health advice.", CancellationToken.None);

        using var body = JsonDocument.Parse(await handler.Requests.Single().Content!.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(350, body.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.True(result.Ok);
        Assert.Equal("메모리를 많이 쓰는 앱을 확인하세요.", result.Text);
    }

    [Fact]
    public async Task Generate_on_model_not_found_hints_at_pull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.NotFound, """{"error":"model 'llama3.2:1b' not found, try pulling it first"}""");

        var result = await Client(handler).GenerateAsync("hi", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("ollama pull", result.Error);
        Assert.Equal(AdviceErrors.ModelMissing, result.Code); // the app says "pull the model" in its own words
    }

    [Fact]
    public async Task Generate_on_empty_response_is_not_ok()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{"response":"","done":true}""");

        var result = await Client(handler).GenerateAsync("hi", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("empty", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AdviceErrors.Empty, result.Code);
    }

    [Fact]
    public async Task Generate_when_server_unreachable_reports_it()
    {
        var result = await Client(new ThrowingHandler()).GenerateAsync("hi", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("reach", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AdviceErrors.Unreachable, result.Code);
    }

    [Fact]
    public async Task A_bare_404_is_not_taken_for_a_missing_model()
    {
        // Another HTTP server at llmAdvisor.endpoint: pulling a model wouldn't help.
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.NotFound, "<html>404 Page</html>");

        var result = await Client(handler).GenerateAsync("hi", CancellationToken.None);

        Assert.Equal(AdviceErrors.ServerError, result.Code);
    }

    [Fact]
    public async Task Generate_on_another_server_error_says_so()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.InternalServerError, """{"error":"out of memory"}""");

        var result = await Client(handler).GenerateAsync("hi", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(AdviceErrors.ServerError, result.Code);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
