using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalOpsBot.Core.Advisor;

namespace LocalOpsBot.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmClient"/> backed by a local Ollama server's <c>/api/generate</c> endpoint
/// (LM Studio exposes a compatible API on its own port). Nothing is bundled — the user runs
/// the server. All failure modes (server down, model not pulled, timeout) are returned as a
/// non-OK <see cref="LlmResult"/> rather than thrown.
/// </summary>
public sealed class OllamaLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;
    private readonly LlmAdvisorOptions _options;

    public OllamaLlmClient(HttpClient http, LlmAdvisorOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<LlmResult> GenerateAsync(string prompt, CancellationToken ct)
    {
        var url = $"{_options.Endpoint.TrimEnd('/')}/api/generate";
        // keep_alive keeps the model warm between calls (a cold load dominates latency for small
        // models); num_predict caps output length so generation stays fast and the reply is short.
        var payload = new
        {
            model = _options.Model,
            prompt,
            stream = false,
            keep_alive = $"{Math.Max(0, _options.KeepAliveMinutes)}m",
            options = new { num_predict = Math.Max(1, _options.MaxTokens) }
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)));

            using var response = await _http.PostAsJsonAsync(url, payload, JsonOpts, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Ollama replies 404 with a helpful "model not found, try pulling it" body.
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                return new LlmResult(false, "",
                    $"LLM server returned {(int)response.StatusCode}. {Summarize(body, _options.Model)}");
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOpts, cts.Token);
            var text = result?.Response?.Trim() ?? "";
            return string.IsNullOrEmpty(text)
                ? new LlmResult(false, "", "The LLM returned an empty response.")
                : new LlmResult(true, text, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmResult(false, "", $"The LLM did not respond within {_options.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            return new LlmResult(false, "",
                $"Could not reach a local LLM at {_options.Endpoint}. Is Ollama running? ({ex.Message})");
        }
    }

    // Turn Ollama's error body into a short, actionable hint.
    private static string Summarize(string body, string model)
    {
        if (body.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return $"The model '{model}' may not be installed — run: ollama pull {model}";
        return body.Length <= 160 ? body.Trim() : body[..160].Trim() + "...";
    }

    private sealed record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("done")] bool Done);
}
