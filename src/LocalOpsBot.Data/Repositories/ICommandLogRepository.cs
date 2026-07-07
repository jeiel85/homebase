namespace LocalOpsBot.Data.Repositories;

public interface ICommandLogRepository
{
    Task<long> InsertAsync(Models.CommandLogEntry entry, CancellationToken ct);
    Task MarkCompletedAsync(long id, string status, string? error, CancellationToken ct);
}
