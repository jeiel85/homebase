using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data.Repositories;

public sealed class AlertLogRepository : IAlertLogRepository
{
    private readonly LocalOpsDbContext _db;

    public AlertLogRepository(LocalOpsDbContext db)
    {
        _db = db;
    }

    public async Task InsertAsync(Models.AlertLogEntry entry, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alert_log (alert_id, kind, severity, title, body, dedup_key, source, status, error, created_at, sent_at)
            VALUES (@alert_id, @kind, @severity, @title, @body, @dedup_key, @source, @status, @error, @created_at, @sent_at)
            """;
        cmd.Parameters.Add(new SqliteParameter("@alert_id", entry.AlertId));
        cmd.Parameters.Add(new SqliteParameter("@kind", entry.Kind));
        cmd.Parameters.Add(new SqliteParameter("@severity", entry.Severity));
        cmd.Parameters.Add(new SqliteParameter("@title", entry.Title));
        cmd.Parameters.Add(new SqliteParameter("@body", (object?)entry.Body ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@dedup_key", (object?)entry.DedupKey ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@source", (object?)entry.Source ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", entry.Status));
        cmd.Parameters.Add(new SqliteParameter("@error", (object?)entry.Error ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@created_at", entry.CreatedAt.ToString("O")));
        cmd.Parameters.Add(new SqliteParameter("@sent_at", entry.SentAt?.ToString("O") ?? (object)DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsRecentDedupKeyAsync(string dedupKey, TimeSpan window, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM alert_log
            WHERE dedup_key = @dedup_key AND created_at >= datetime('now', @window)
            """;
        cmd.Parameters.Add(new SqliteParameter("@dedup_key", dedupKey));
        cmd.Parameters.Add(new SqliteParameter("@window", $"-{window.TotalSeconds:F0} seconds"));
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }
}
