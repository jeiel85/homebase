using LocalOpsBot.Data.Models;
using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data.Repositories;

public sealed class WatchStatusRepository : IWatchStatusRepository
{
    private readonly LocalOpsDbContext _db;

    public WatchStatusRepository(LocalOpsDbContext db) => _db = db;

    public async Task InsertAsync(WatchStatusEntry entry, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO watch_status (watch_name, watch_type, status, status_json, changed_at)
            VALUES (@watch_name, @watch_type, @status, @status_json, @changed_at)
            """;
        cmd.Parameters.Add(new SqliteParameter("@watch_name", entry.WatchName));
        cmd.Parameters.Add(new SqliteParameter("@watch_type", entry.WatchType));
        cmd.Parameters.Add(new SqliteParameter("@status", entry.Status));
        cmd.Parameters.Add(new SqliteParameter("@status_json", (object?)entry.StatusJson ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@changed_at", entry.ChangedAt.ToString("O")));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<WatchStatusEntry?> GetLatestAsync(string watchName, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, watch_name, watch_type, status, status_json, changed_at FROM watch_status WHERE watch_name = @watch_name ORDER BY changed_at DESC LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@watch_name", watchName));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new WatchStatusEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)));
        }
        return null;
    }

    public async Task<IReadOnlyList<WatchStatusEntry>> GetRecentAsync(int count, CancellationToken ct)
    {
        var results = new List<WatchStatusEntry>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, watch_name, watch_type, status, status_json, changed_at FROM watch_status ORDER BY changed_at DESC LIMIT @count";
        cmd.Parameters.Add(new SqliteParameter("@count", count));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new WatchStatusEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }
        return results;
    }
}
