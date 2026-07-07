using LocalOpsBot.Data;
using LocalOpsBot.Data.Repositories;
using Xunit;

namespace LocalOpsBot.Tests.Data.Repositories;

public sealed class RuntimeStateRepositoryTests : IDisposable
{
    private readonly LocalOpsDbContext _db;

    public RuntimeStateRepositoryTests()
    {
        _db = new LocalOpsDbContext("Data Source=:memory:");
        _db.Connection.Open();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS runtime_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SetAndGet_roundtrip()
    {
        var repo = new RuntimeStateRepository(_db);
        await repo.SetAsync("test_key", "test_value", default);
        var result = await repo.GetAsync("test_key", default);
        Assert.Equal("test_value", result);
    }

    [Fact]
    public async Task Get_returns_null_for_missing_key()
    {
        var repo = new RuntimeStateRepository(_db);
        var result = await repo.GetAsync("nonexistent", default);
        Assert.Null(result);
    }

    [Fact]
    public async Task Set_overwrites_existing_value()
    {
        var repo = new RuntimeStateRepository(_db);
        await repo.SetAsync("key", "first", default);
        await repo.SetAsync("key", "second", default);
        var result = await repo.GetAsync("key", default);
        Assert.Equal("second", result);
    }

    [Fact]
    public async Task Set_and_get_multiple_keys()
    {
        var repo = new RuntimeStateRepository(_db);
        await repo.SetAsync("k1", "v1", default);
        await repo.SetAsync("k2", "v2", default);

        Assert.Equal("v1", await repo.GetAsync("k1", default));
        Assert.Equal("v2", await repo.GetAsync("k2", default));
    }
}
