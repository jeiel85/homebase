using LocalOpsBot.Data.Models;
using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data.Repositories;

public sealed class NotificationEventRepository : INotificationEventRepository
{
    private readonly LocalOpsDbContext _db;

    public NotificationEventRepository(LocalOpsDbContext db) => _db = db;

    public async Task InsertAsync(NotificationEventEntry entry, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notification_event (event_id, source_app, title, body, body_hash, sensitivity, forwarded, dropped_reason, created_at, processed_at)
            VALUES (@event_id, @source_app, @title, @body, @body_hash, @sensitivity, @forwarded, @dropped_reason, @created_at, @processed_at)
            """;
        cmd.Parameters.Add(new SqliteParameter("@event_id", entry.EventId));
        cmd.Parameters.Add(new SqliteParameter("@source_app", entry.SourceApp));
        cmd.Parameters.Add(new SqliteParameter("@title", (object?)entry.Title ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@body", (object?)entry.Body ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@body_hash", (object?)entry.BodyHash ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@sensitivity", entry.Sensitivity));
        cmd.Parameters.Add(new SqliteParameter("@forwarded", entry.Forwarded ? 1 : 0));
        cmd.Parameters.Add(new SqliteParameter("@dropped_reason", (object?)entry.DroppedReason ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@created_at", entry.CreatedAt.ToString("O")));
        cmd.Parameters.Add(new SqliteParameter("@processed_at", entry.ProcessedAt.ToString("O")));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationEventEntry>> GetRecentAsync(int count, CancellationToken ct)
    {
        var results = new List<NotificationEventEntry>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, event_id, source_app, title, body, body_hash, sensitivity, forwarded, dropped_reason, created_at, processed_at FROM notification_event ORDER BY created_at DESC LIMIT @count";
        cmd.Parameters.Add(new SqliteParameter("@count", count));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new NotificationEventEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6), reader.GetInt32(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                DateTimeOffset.Parse(reader.GetString(9)),
                DateTimeOffset.Parse(reader.GetString(10))));
        }
        return results;
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM notification_event WHERE processed_at < @cutoff";
        cmd.Parameters.Add(new SqliteParameter("@cutoff", cutoff.ToString("O")));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
