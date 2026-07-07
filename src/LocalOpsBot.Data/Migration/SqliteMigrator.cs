using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Data.Migration;

public sealed class SqliteMigrator : IDatabaseMigrator
{
    private readonly LocalOpsDbContext _db;
    private readonly ILogger<SqliteMigrator> _logger;

    public SqliteMigrator(LocalOpsDbContext db, ILogger<SqliteMigrator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        _logger.LogInformation("SQLite database opened, running migrations");

        await _db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """, ct);

        var version = await GetCurrentVersionAsync(ct);
        _logger.LogInformation("Current schema version: {Version}", version);

        if (version < 1)
            await ApplyV1Async(ct);
    }

    private async Task<int> GetCurrentVersionAsync(CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private async Task ApplyV1Async(CancellationToken ct)
    {
        _logger.LogInformation("Applying migration V1: initial schema");

        await _db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS runtime_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS command_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id INTEGER NOT NULL,
                user_id INTEGER,
                command TEXT NOT NULL,
                args_json TEXT,
                raw_text TEXT,
                status TEXT NOT NULL,
                error TEXT,
                received_at TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_command_log_received_at ON command_log(received_at);
            CREATE INDEX IF NOT EXISTS ix_command_log_command ON command_log(command);

            CREATE TABLE IF NOT EXISTS alert_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                alert_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                severity TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT,
                dedup_key TEXT,
                source TEXT,
                status TEXT NOT NULL,
                error TEXT,
                created_at TEXT NOT NULL,
                sent_at TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_alert_log_created_at ON alert_log(created_at);
            CREATE INDEX IF NOT EXISTS ix_alert_log_dedup_key ON alert_log(dedup_key);
            CREATE INDEX IF NOT EXISTS ix_alert_log_kind ON alert_log(kind);

            CREATE TABLE IF NOT EXISTS metric_sample (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                collected_at TEXT NOT NULL,
                cpu_usage_percent REAL,
                memory_usage_percent REAL,
                total_memory_bytes INTEGER,
                available_memory_bytes INTEGER,
                uptime_seconds INTEGER,
                disk_json TEXT,
                network_json TEXT
            );

            CREATE TABLE IF NOT EXISTS notification_event (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL,
                source_app TEXT NOT NULL,
                title TEXT,
                body TEXT,
                body_hash TEXT,
                sensitivity TEXT NOT NULL,
                forwarded INTEGER NOT NULL,
                dropped_reason TEXT,
                created_at TEXT NOT NULL,
                processed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS watch_status (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                watch_name TEXT NOT NULL,
                watch_type TEXT NOT NULL,
                status TEXT NOT NULL,
                status_json TEXT,
                changed_at TEXT NOT NULL
            );
            """, ct);

        using var insertCmd = _db.Connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES (1, datetime('now'))";
        await insertCmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Migration V1 complete");
    }
}
