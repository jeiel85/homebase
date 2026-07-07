namespace LocalOpsBot.Data.Repositories;

public interface IRuntimeStateRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}
