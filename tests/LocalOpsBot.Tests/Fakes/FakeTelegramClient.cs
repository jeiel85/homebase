using LocalOpsBot.Infrastructure.Telegram;

namespace LocalOpsBot.Tests.Fakes;

internal class FakeTelegramClient : ITelegramClient
{
    public List<(long ChatId, string Text, TelegramSendOptions? Options)> Sent { get; } = new();
    public Queue<IReadOnlyList<TelegramUpdate>> UpdateQueue { get; } = new();

    public virtual Task SendMessageAsync(long chatId, string text, TelegramSendOptions? options, CancellationToken ct)
    {
        Sent.Add((chatId, text, options));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long? offset, int timeoutSeconds, CancellationToken ct)
    {
        return Task.FromResult(UpdateQueue.TryDequeue(out var result) ? result : Array.Empty<TelegramUpdate>());
    }
}
