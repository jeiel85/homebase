using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalOpsBot.Infrastructure.Llm;

/// <summary>How ready the local Ollama server is to serve advice.</summary>
public enum OllamaReadiness
{
    /// <summary>The server could not be reached (not installed, not started, or wrong endpoint).</summary>
    Unreachable,

    /// <summary>The server is up but the configured model has not been pulled yet.</summary>
    ModelMissing,

    /// <summary>The server is up and the configured model is installed.</summary>
    Ready
}

/// <summary>Result of a readiness probe, with the models the server reported (for guidance).</summary>
public sealed record OllamaReadinessResult(
    OllamaReadiness Status, string? Detail, IReadOnlyList<string> InstalledModels);

/// <summary>
/// Probes a local Ollama server's <c>/api/tags</c> endpoint to tell whether it is running and
/// whether a given model is installed — used to guide the user through setup. Never throws: any
/// connection or parsing failure maps to <see cref="OllamaReadiness.Unreachable"/>.
/// </summary>
public sealed class OllamaReadinessChecker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;

    public OllamaReadinessChecker(HttpClient http) => _http = http;

    public async Task<OllamaReadinessResult> CheckAsync(string endpoint, string model, CancellationToken ct)
    {
        var url = $"{endpoint.TrimEnd('/')}/api/tags";
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new OllamaReadinessResult(
                    OllamaReadiness.Unreachable, $"Server returned {(int)response.StatusCode}.", []);

            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(JsonOpts, ct);
            var models = tags?.Models?
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList() ?? [];

            return models.Any(installed => ModelMatches(installed, model))
                ? new OllamaReadinessResult(OllamaReadiness.Ready, null, models)
                : new OllamaReadinessResult(OllamaReadiness.ModelMissing, $"Model '{model}' is not pulled.", models);
        }
        catch (Exception ex)
        {
            // Connection refused, DNS failure, timeout, malformed JSON: all mean "not usable yet".
            return new OllamaReadinessResult(OllamaReadiness.Unreachable, ex.Message, []);
        }
    }

    // Ollama tags look like "llama3.2:1b". Match the configured model exactly, or by base name
    // when the user configured a bare name (no tag) so "llama3.2" accepts "llama3.2:latest".
    private static bool ModelMatches(string installed, string wanted)
    {
        if (string.Equals(installed, wanted, StringComparison.OrdinalIgnoreCase))
            return true;
        if (wanted.Contains(':'))
            return false;
        var installedBase = installed.Split(':')[0];
        return string.Equals(installedBase, wanted, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<TagModel>? Models);

    private sealed record TagModel(
        [property: JsonPropertyName("name")] string? Name);
}
