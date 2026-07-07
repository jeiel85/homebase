using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Data.Repositories;

namespace LocalOpsBot.Data.Adapters;

public sealed class StateStoreAdapter : IStateStore
{
    private readonly IRuntimeStateRepository _inner;

    public StateStoreAdapter(IRuntimeStateRepository inner) => _inner = inner;

    public Task<string?> GetAsync(string key, CancellationToken ct)
        => _inner.GetAsync(key, ct);

    public Task SetAsync(string key, string value, CancellationToken ct)
        => _inner.SetAsync(key, value, ct);
}
