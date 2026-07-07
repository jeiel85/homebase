namespace LocalOpsBot.Infrastructure.Telegram;

public sealed class TelegramApiException : Exception
{
    public int HttpStatusCode { get; }
    public string? ApiDescription { get; }

    public TelegramApiException(int httpStatusCode, string message, string? apiDescription = null)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
        ApiDescription = apiDescription;
    }
}
