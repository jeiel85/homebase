using LocalOpsBot.Agent.Services;
using LocalOpsBot.Infrastructure.Telegram;
using Xunit;

namespace LocalOpsBot.Tests.Agent.Services;

public sealed class TelegramPollingServiceTests
{
    public static TheoryData<Exception> TransientErrors => new()
    {
        new HttpRequestException("No such host is known. (api.telegram.org:443)"),
        // HttpClient.Timeout elapsing surfaces as a cancellation while the Agent keeps running.
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."),
        new IOException("Unable to read data from the transport connection."),
        new TelegramApiException(502, "Telegram getUpdates failed (502): Bad Gateway"),
        new TelegramApiException(429, "Telegram getUpdates failed (429): Too Many Requests"),
    };

    public static TheoryData<Exception> RealErrors => new()
    {
        new TelegramApiException(409, "Telegram getUpdates failed (409): Conflict"),
        new TelegramApiException(400, "Telegram getUpdates failed (400): Bad Request"),
        new System.Text.Json.JsonException("bad payload"),
        new InvalidOperationException("database is locked"),
    };

    [Theory]
    [MemberData(nameof(TransientErrors))]
    public void Network_blips_are_transient(Exception ex)
        => Assert.True(TelegramPollingService.IsTransient(ex, CancellationToken.None));

    [Theory]
    [MemberData(nameof(RealErrors))]
    public void Other_failures_stay_errors(Exception ex)
        => Assert.False(TelegramPollingService.IsTransient(ex, CancellationToken.None));

    [Fact]
    public void Cancellation_while_stopping_is_not_a_transient_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(TelegramPollingService.IsTransient(new OperationCanceledException(cts.Token), cts.Token));
    }
}
