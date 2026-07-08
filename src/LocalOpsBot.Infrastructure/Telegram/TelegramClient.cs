using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Infrastructure.Telegram;

public sealed class TelegramClient : ITelegramClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TelegramClient(HttpClient http, IOptions<TelegramOptions> options)
    {
        var token = options.Value.BotToken;
        // Support "ENV:VARNAME" indirection so the secret can live in an environment
        // variable instead of being written in plain text into the config file.
        if (!string.IsNullOrEmpty(token) && token.StartsWith("ENV:", StringComparison.Ordinal))
            token = Environment.GetEnvironmentVariable(token.Substring(4)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Telegram bot token is not configured.");

        _http = http;
        _baseUrl = $"https://api.telegram.org/bot{token}";
    }

    public async Task SendMessageAsync(
        long chatId, string text, TelegramSendOptions? sendOptions, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = text
        };

        if (sendOptions != null)
        {
            payload["parse_mode"] = sendOptions.ParseMode;
            payload["disable_web_page_preview"] = sendOptions.DisableWebPagePreview;
            payload["disable_notification"] = sendOptions.DisableNotification;
        }

        using var response = await _http.PostAsJsonAsync(
            $"{_baseUrl}/sendMessage", payload, JsonOpts, ct);

        if (!response.IsSuccessStatusCode)
            await ThrowOnError(response, "sendMessage");
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long? offset, int timeoutSeconds, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new[] { "message" }
        };

        using var response = await _http.PostAsJsonAsync(
            $"{_baseUrl}/getUpdates", payload, JsonOpts, ct);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowOnError(response, "getUpdates");
            return Array.Empty<TelegramUpdate>();
        }

        var wrapper = await response.Content.ReadFromJsonAsync<TelegramResponse>(JsonOpts, ct);
        return wrapper?.Result ?? Array.Empty<TelegramUpdate>();
    }

    private static async Task ThrowOnError(HttpResponseMessage response, string apiMethod)
    {
        var body = await response.Content.ReadAsStringAsync();

        string description;
        try
        {
            var err = JsonSerializer.Deserialize<TelegramErrorResponse>(body, JsonOpts);
            description = err?.Description ?? body;
        }
        catch
        {
            description = body;
        }

        throw new TelegramApiException(
            (int)response.StatusCode,
            $"Telegram {apiMethod} failed ({(int)response.StatusCode}): {description}",
            apiMethod);
    }

    private sealed record TelegramResponse(bool Ok, IReadOnlyList<TelegramUpdate>? Result);
    private sealed record TelegramErrorResponse(bool Ok, int ErrorCode, string? Description);
}
