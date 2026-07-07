using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data;

public sealed class LocalOpsDbContext : IDisposable
{
    private readonly SqliteConnection _connection;

    public LocalOpsDbContext(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _connection.OpenAsync(ct);

        const string sql = """
            CREATE TABLE IF NOT EXISTS runtime_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS command_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                command_name TEXT NOT NULL,
                chat_id INTEGER NOT NULL,
                user_id INTEGER,
                raw_text TEXT,
                status TEXT NOT NULL DEFAULT 'Received',
                error TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS alert_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                alert_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                severity TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT,
                dedup_key TEXT,
                source TEXT,
                status TEXT NOT NULL DEFAULT 'Created',
                error TEXT,
                created_at TEXT NOT NULL,
                sent_at TEXT
            );
            """;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose() => _connection?.Dispose();
}
