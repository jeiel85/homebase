using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data.Repositories;

public sealed class CommandLogRepository : ICommandLogRepository
{
    private readonly LocalOpsDbContext _db;

    public CommandLogRepository(LocalOpsDbContext db)
    {
        _db = db;
    }

    public async Task<long> InsertAsync(Models.CommandLogEntry entry, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO command_log (chat_id, user_id, command, args_json, raw_text, status, error, received_at, completed_at)
            VALUES (@chat_id, @user_id, @command, @args_json, @raw_text, @status, @error, @received_at, @completed_at);
            SELECT last_insert_rowid()
            """;
        cmd.Parameters.Add(new SqliteParameter("@chat_id", entry.ChatId));
        cmd.Parameters.Add(new SqliteParameter("@user_id", (object?)entry.UserId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@command", entry.Command));
        cmd.Parameters.Add(new SqliteParameter("@args_json", (object?)entry.ArgsJson ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@raw_text", (object?)entry.RawText ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", entry.Status));
        cmd.Parameters.Add(new SqliteParameter("@error", (object?)entry.Error ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@received_at", entry.ReceivedAt.ToString("O")));
        cmd.Parameters.Add(new SqliteParameter("@completed_at", entry.CompletedAt?.ToString("O") ?? (object)DBNull.Value));
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task MarkCompletedAsync(long id, string status, string? error, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE command_log SET status = @status, error = @error, completed_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@status", status));
        cmd.Parameters.Add(new SqliteParameter("@error", (object?)error ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
