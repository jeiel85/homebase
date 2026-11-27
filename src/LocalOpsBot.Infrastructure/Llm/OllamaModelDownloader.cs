using System.Net.Http.Json;
using System.Text.Json;

namespace LocalOpsBot.Infrastructure.Llm;

public sealed record OllamaPullProgress(string Status, long Completed, long Total);

/// <summary>Streams Ollama's download progress; only an explicit success ends a download successfully.</summary>
public sealed class OllamaModelDownloader(HttpClient http)
{
    public async Task PullAsync(string endpoint, string model, IProgress<OllamaPullProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString() ?? "Ollama download failed.");
            var status = root.TryGetProperty("status", out var value) ? value.GetString() ?? "" : "";
            var completed = root.TryGetProperty("completed", out var c) ? c.GetInt64() : 0;
            var total = root.TryGetProperty("total", out var t) ? t.GetInt64() : 0;
            progress?.Report(new OllamaPullProgress(status, completed, total));
            if (status == "success") return;
        }
        throw new IOException("Ollama closed the download before reporting success.");
    }
}
