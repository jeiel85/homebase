using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data;

public sealed class LocalOpsDbContext : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteConnection Connection => _connection;

    /// <summary>The database file, or null for an in-memory database.</summary>
    public string? FilePath
    {
        get
        {
            var source = new SqliteConnectionStringBuilder(_connection.ConnectionString).DataSource;
            return string.IsNullOrEmpty(source) || source == ":memory:" ? null : Path.GetFullPath(source);
        }
    }

    public LocalOpsDbContext(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
    }

    /// <summary>
    /// Exclusive use of <see cref="Connection"/> until the returned handle is disposed. The one
    /// connection is shared by every repository, and a <see cref="SqliteConnection"/> is not safe for
    /// concurrent use, while the alert, state, watch and history stores are called from several
    /// background services at once (#45). Hold it across the whole command, reader included. Not
    /// re-entrant: never call another repository method while holding it.
    /// </summary>
    public async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
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

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
