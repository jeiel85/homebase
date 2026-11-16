using LocalOpsBot.Data;
using LocalOpsBot.Data.Models;
using LocalOpsBot.Data.Repositories;
using Xunit;

namespace LocalOpsBot.Tests.Data.Repositories;

public sealed class AlertLogRepositoryTests : IDisposable
{
    private readonly LocalOpsDbContext _db;

    public AlertLogRepositoryTests()
    {
        _db = new LocalOpsDbContext("Data Source=:memory:");
        _db.Connection.Open();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
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
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();

    private AlertLogEntry SampleEntry(int seed = 1) => new(
        null, $"alert-{seed}", "test", "Warning", $"Test Title {seed}",
        $"Body {seed}", $"dedup-{seed}", "test-source",
        "Sent", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Insert_and_get_recent()
    {
        var repo = new AlertLogRepository(_db);
        await repo.InsertAsync(SampleEntry(1), default);
        await repo.InsertAsync(SampleEntry(2), default);

        var recent = await repo.GetRecentAsync(10, default);
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task GetRecent_respects_count()
    {
        var repo = new AlertLogRepository(_db);
        for (int i = 1; i <= 5; i++)
            await repo.InsertAsync(SampleEntry(i), default);

        var recent = await repo.GetRecentAsync(3, default);
        Assert.Equal(3, recent.Count);
    }

    [Fact]
    public async Task GetRecent_orders_by_created_at_descending()
    {
        var repo = new AlertLogRepository(_db);
        await repo.InsertAsync(SampleEntry(1), default);
        await repo.InsertAsync(SampleEntry(2), default);

        var recent = await repo.GetRecentAsync(10, default);
        Assert.Contains("Test Title 1", recent[1].Title);
    }

    [Fact]
    public async Task ExistsRecentDedupKey_returns_true_when_exists()
    {
        var repo = new AlertLogRepository(_db);
        await repo.InsertAsync(SampleEntry(1), default);

        var exists = await repo.ExistsRecentDedupKeyAsync("dedup-1", TimeSpan.FromDays(1), default);
        Assert.True(exists);
    }

    [Theory]
    // An alert older than the window no longer suppresses the next one, whatever offset it was stored with.
    [InlineData(-20, 0, false)]
    [InlineData(-20, 9, false)]
    [InlineData(-5, 0, true)]
    [InlineData(-5, 9, true)]
    public async Task ExistsRecentDedupKey_honours_the_window(int minutesAgo, int offsetHours, bool expected)
    {
        var repo = new AlertLogRepository(_db);
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(minutesAgo).ToOffset(TimeSpan.FromHours(offsetHours));
        await repo.InsertAsync(SampleEntry(1) with { CreatedAt = createdAt }, default);

        var exists = await repo.ExistsRecentDedupKeyAsync("dedup-1", TimeSpan.FromMinutes(10), default);
        Assert.Equal(expected, exists);
    }

    [Fact]
    public async Task ExistsRecentDedupKey_returns_false_when_not_exists()
    {
        var repo = new AlertLogRepository(_db);
        await repo.InsertAsync(SampleEntry(1), default);

        var exists = await repo.ExistsRecentDedupKeyAsync("nonexistent", TimeSpan.FromDays(1), default);
        Assert.False(exists);
    }

    [Fact]
    public async Task Insert_with_null_body()
    {
        var repo = new AlertLogRepository(_db);
        var entry = new AlertLogEntry(
            null, "alert-null", "test", "Info", "No Body",
            null, null, null, "Sent", null,
            DateTimeOffset.UtcNow, null);

        await repo.InsertAsync(entry, default);
        var recent = await repo.GetRecentAsync(1, default);
        Assert.Single(recent);
        Assert.Equal("alert-null", recent[0].AlertId);
    }

    [Fact]
    public async Task MarkSent_turns_a_failed_row_into_sent_and_keeps_the_first_error()
    {
        var repo = new AlertLogRepository(_db);
        await repo.InsertAsync(SampleEntry(1) with { Status = "Failed", Error = "telegram: host unknown", SentAt = null }, default);
        var sentAt = DateTimeOffset.UtcNow;

        await repo.MarkSentAsync("alert-1", sentAt, default);

        var row = Assert.Single(await repo.GetRecentAsync(10, default));
        Assert.Equal("Sent", row.Status);
        Assert.Equal("telegram: host unknown", row.Error);
        Assert.Equal(sentAt, row.SentAt!.Value);
    }

    [Fact]
    public async Task Concurrent_calls_on_the_shared_connection_are_serialized()
    {
        // Several Agent services write alerts, state and history on the one connection at once (#45).
        var repo = new AlertLogRepository(_db);
        var state = new RuntimeStateRepository(_db);
        using (var create = _db.Connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS runtime_state (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        var tasks = Enumerable.Range(1, 40).Select(i => Task.Run(async () =>
        {
            await repo.InsertAsync(SampleEntry(i), default);
            await state.SetAsync($"k{i % 5}", i.ToString(), default);
            await repo.ExistsRecentDedupKeyAsync($"dedup-{i}", TimeSpan.FromDays(1), default);
            await repo.GetRecentAsync(5, default);
        }));
        await Task.WhenAll(tasks);

        Assert.Equal(40, (await repo.GetRecentAsync(100, default)).Count);
    }
}
