using System.Globalization;
using LocalOpsBot.Core.Notifications;
using Microsoft.Data.Sqlite;

namespace LocalOpsBot.Data.Repositories;

/// <summary>
/// The notification center's store (DECISIONS M-32): relayed notifications in
/// <c>notification_history</c>, merged with this PC's <c>alert_log</c> when listed.
/// Times are stored as UTC round-trip strings, so string order is time order (the alert log is
/// written the same way). Calls are serialized here because the relay, the Tray's requests and the
/// daily prune can arrive together on the one shared connection.
/// </summary>
public sealed class NotificationHistoryRepository : INotificationHistoryStore
{
    private readonly LocalOpsDbContext _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NotificationHistoryRepository(LocalOpsDbContext db) => _db = db;

    private static string Stamp(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public async Task AddAsync(DeviceNotificationRecord r, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO notification_history (notif_id, origin_id, origin_name, origin_kind, app, title, body, ts, received_at)
                VALUES (@notif_id, @origin_id, @origin_name, @origin_kind, @app, @title, @body, @ts, @received_at)
                """;
            cmd.Parameters.Add(new SqliteParameter("@notif_id", (object?)r.NotifId ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@origin_id", r.OriginId));
            cmd.Parameters.Add(new SqliteParameter("@origin_name", r.OriginName));
            cmd.Parameters.Add(new SqliteParameter("@origin_kind", r.OriginKind));
            cmd.Parameters.Add(new SqliteParameter("@app", r.App));
            cmd.Parameters.Add(new SqliteParameter("@title", r.Title));
            cmd.Parameters.Add(new SqliteParameter("@body", (object?)r.Body ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@ts", Stamp(r.Ts)));
            cmd.Parameters.Add(new SqliteParameter("@received_at", Stamp(r.ReceivedAt)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<HistoryPage> ListAsync(HistoryCursor? after, int limit, CancellationToken ct)
    {
        limit = NotificationHistoryRules.ClampLimit(limit);
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _db.Connection.CreateCommand();
            // Fetch one extra row to know whether an older page exists.
            cmd.CommandText = """
                SELECT kind, id, at, source, source_kind, origin_id, app, title, body, severity, status FROM (
                    SELECT 'device' AS kind, id, received_at AS at, origin_name AS source, origin_kind AS source_kind,
                           origin_id, app, title, substr(body, 1, @max_body) AS body, NULL AS severity, NULL AS status
                    FROM notification_history
                    WHERE @at IS NULL OR received_at <= @at
                    UNION ALL
                    SELECT 'alert', id, created_at, NULL, NULL, NULL, kind, title, substr(body, 1, @max_body), severity, status
                    FROM alert_log
                    WHERE @at IS NULL OR created_at <= @at
                )
                WHERE @at IS NULL OR at < @at OR (at = @at AND (kind < @kind OR (kind = @kind AND id < @id)))
                ORDER BY at DESC, kind DESC, id DESC
                LIMIT @take
                """;
            cmd.Parameters.Add(new SqliteParameter("@at", after is { } a ? Stamp(a.At) : DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@kind", (object?)after?.Kind ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@id", (object?)after?.Id ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@take", limit + 1));
            cmd.Parameters.Add(new SqliteParameter("@max_body", NotificationHistoryRules.MaxListedBody));
            var entries = new List<HistoryEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string? Opt(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                entries.Add(new HistoryEntry(
                    reader.GetString(0), reader.GetInt64(1), ParseStamp(reader.GetString(2)),
                    Opt(3), Opt(4), Opt(5), reader.GetString(6), reader.GetString(7), Opt(8), Opt(9), Opt(10)));
            }
            var more = entries.Count > limit;
            if (more) entries.RemoveAt(entries.Count - 1);
            return new HistoryPage(entries, more);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> ClearDevicesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM notification_history";
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> PruneAsync(DateTimeOffset cutoff, int maxDeviceEntries, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM notification_history WHERE received_at < @cutoff;
                DELETE FROM notification_history WHERE id NOT IN (
                    SELECT id FROM notification_history ORDER BY received_at DESC, id DESC LIMIT @max);
                DELETE FROM alert_log WHERE created_at < @cutoff;
                """;
            cmd.Parameters.Add(new SqliteParameter("@cutoff", Stamp(cutoff)));
            cmd.Parameters.Add(new SqliteParameter("@max", Math.Max(0, maxDeviceEntries)));
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }
}
