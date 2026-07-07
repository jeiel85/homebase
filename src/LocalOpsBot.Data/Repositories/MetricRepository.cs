using LocalOpsBot.Data.Models;
using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data.Repositories;

public sealed class MetricRepository : IMetricRepository
{
    private readonly LocalOpsDbContext _db;

    public MetricRepository(LocalOpsDbContext db) => _db = db;

    public async Task InsertAsync(MetricSampleEntry entry, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metric_sample (collected_at, cpu_usage_percent, memory_usage_percent, total_memory_bytes, available_memory_bytes, uptime_seconds, disk_json, network_json)
            VALUES (@collected_at, @cpu, @mem, @total_mem, @avail_mem, @uptime, @disk_json, @network_json)
            """;
        cmd.Parameters.Add(new SqliteParameter("@collected_at", entry.CollectedAt.ToString("O")));
        cmd.Parameters.AddRange([
            new SqliteParameter("@cpu", (object?)entry.CpuUsagePercent ?? DBNull.Value),
            new SqliteParameter("@mem", (object?)entry.MemoryUsagePercent ?? DBNull.Value),
            new SqliteParameter("@total_mem", (object?)entry.TotalMemoryBytes ?? DBNull.Value),
            new SqliteParameter("@avail_mem", (object?)entry.AvailableMemoryBytes ?? DBNull.Value),
            new SqliteParameter("@uptime", (object?)entry.UptimeSeconds ?? DBNull.Value),
            new SqliteParameter("@disk_json", (object?)entry.DiskJson ?? DBNull.Value),
            new SqliteParameter("@network_json", (object?)entry.NetworkJson ?? DBNull.Value)
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MetricSampleEntry>> GetRecentAsync(int count, CancellationToken ct)
    {
        var results = new List<MetricSampleEntry>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, collected_at, cpu_usage_percent, memory_usage_percent, total_memory_bytes, available_memory_bytes, uptime_seconds, disk_json, network_json FROM metric_sample ORDER BY collected_at DESC LIMIT @count";
        cmd.Parameters.Add(new SqliteParameter("@count", count));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MetricSampleEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return results;
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM metric_sample WHERE collected_at < @cutoff";
        cmd.Parameters.Add(new SqliteParameter("@cutoff", cutoff.ToString("O")));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
