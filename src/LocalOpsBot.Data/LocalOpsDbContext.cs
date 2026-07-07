using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data;

public sealed class LocalOpsDbContext : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteConnection Connection => _connection;

    public LocalOpsDbContext(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
    }

    public async Task OpenAsync(CancellationToken ct)
    {
        await _connection.OpenAsync(ct);
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose() => _connection.Dispose();
}
