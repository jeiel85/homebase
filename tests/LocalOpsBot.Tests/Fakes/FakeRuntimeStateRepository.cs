using LocalOpsBot.Data.Repositories;

namespace LocalOpsBot.Tests.Fakes;

public sealed class FakeRuntimeStateRepository : IRuntimeStateRepository
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        _store.TryGetValue(key, out var value);
        return Task.FromResult<string?>(value);
    }

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }
}
