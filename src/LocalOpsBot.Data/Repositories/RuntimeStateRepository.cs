using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data.Repositories;

public sealed class RuntimeStateRepository : IRuntimeStateRepository
{
    private readonly LocalOpsDbContext _db;

    public RuntimeStateRepository(LocalOpsDbContext db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM runtime_state WHERE key = @key";
        cmd.Parameters.Add(new SqliteParameter("@key", key));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runtime_state (key, value, updated_at)
            VALUES (@key, @value, datetime('now'))
            ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = datetime('now')
            """;
        cmd.Parameters.Add(new SqliteParameter("@key", key));
        cmd.Parameters.Add(new SqliteParameter("@value", value));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
