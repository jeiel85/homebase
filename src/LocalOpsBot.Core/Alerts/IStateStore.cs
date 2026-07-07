namespace LocalOpsBot.Core.Alerts;

public interface IStateStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}
