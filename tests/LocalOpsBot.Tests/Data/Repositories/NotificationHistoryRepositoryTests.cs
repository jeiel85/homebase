using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Data;
using LocalOpsBot.Data.Migration;
using LocalOpsBot.Data.Models;
using LocalOpsBot.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Data.Repositories;

/// <summary>An in-memory database migrated to the current schema, for notification-center tests.</summary>
internal sealed class HistoryTestDb : IDisposable
{
    public LocalOpsDbContext Db { get; } = new("Data Source=:memory:");
    public NotificationHistoryRepository History { get; }
    public AlertLogRepository Alerts { get; }

    private HistoryTestDb()
    {
        History = new NotificationHistoryRepository(Db);
        Alerts = new AlertLogRepository(Db);
    }

    public static async Task<HistoryTestDb> CreateAsync()
    {
        var t = new HistoryTestDb();
        await new SqliteMigrator(t.Db, NullLogger<SqliteMigrator>.Instance).MigrateAsync(CancellationToken.None);
        return t;
    }

    public void Dispose() => Db.Dispose();
}

public sealed class NotificationHistoryRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private HistoryTestDb _t = null!;

    public async Task InitializeAsync() => _t = await HistoryTestDb.CreateAsync();

    public Task DisposeAsync()
    {
        _t.Dispose();
        return Task.CompletedTask;
    }

    private Task Relay(string title, DateTimeOffset at, string origin = "Galaxy S24", string kind = DeviceKind.Phone, string? body = null) =>
        _t.History.AddAsync(new DeviceNotificationRecord($"n-{title}", $"id-{origin}", origin, kind, "카카오톡", title, body, at.AddSeconds(-2), at),
            CancellationToken.None);

    private Task Alert(string title, DateTimeOffset at, string status = "Sent", string? body = null) =>
        _t.Alerts.InsertAsync(new AlertLogEntry(null, Guid.NewGuid().ToString("N"), "event_log", "Warning", title, body,
            null, "HOME-PC", status, null, at, status == "Sent" ? at : null), CancellationToken.None);

    [Fact]
    public async Task Migration_adds_version_2_and_running_it_again_changes_nothing()
    {
        await new SqliteMigrator(_t.Db, NullLogger<SqliteMigrator>.Instance).MigrateAsync(CancellationToken.None);

        using var cmd = _t.Db.Connection.CreateCommand();
        cmd.CommandText = "SELECT group_concat(version) FROM (SELECT version FROM schema_version ORDER BY version)";
        Assert.Equal("1,2", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Relayed_notifications_and_alerts_come_back_merged_newest_first()
    {
        await Relay("엄마", T0, body: "밥 먹었니?");
        await Alert("디스크 오류", T0.AddMinutes(1), status: "Failed", body: "Event 7");
        await Relay("deploy done", T0.AddMinutes(2), origin: "WORK-PC", kind: DeviceKind.Pc);

        var page = await _t.History.ListAsync(null, 10, CancellationToken.None);

        Assert.False(page.More);
        Assert.Equal(new[] { "deploy done", "디스크 오류", "엄마" }, page.Entries.Select(e => e.Title));

        var pc = page.Entries[0];
        Assert.Equal(HistoryKind.Device, pc.Kind);
        Assert.Equal("WORK-PC", pc.Source);
        Assert.Equal(DeviceKind.Pc, pc.SourceKind);
        Assert.Equal(T0.AddMinutes(2), pc.At);

        var alert = page.Entries[1];
        Assert.Equal(HistoryKind.Alert, alert.Kind);
        Assert.Equal("event_log", alert.App);
        Assert.Equal("Warning", alert.Severity);
        Assert.Equal("Failed", alert.Status);
        Assert.Equal("Event 7", alert.Body);
        Assert.Null(alert.Source);

        var phone = page.Entries[2];
        Assert.Equal("Galaxy S24", phone.Source);
        Assert.Equal(DeviceKind.Phone, phone.SourceKind);
        Assert.Equal("밥 먹었니?", phone.Body);
    }

    [Fact]
    public async Task Pages_continue_before_the_last_entry_shown()
    {
        for (var i = 0; i < 5; i++) await Relay($"r{i}", T0.AddMinutes(i));
        await Alert("a", T0.AddMinutes(2.5));

        var first = await _t.History.ListAsync(null, 3, CancellationToken.None);
        Assert.True(first.More);
        Assert.Equal(new[] { "r4", "r3", "a" }, first.Entries.Select(e => e.Title));

        var second = await _t.History.ListAsync(first.Entries[^1].At, 3, CancellationToken.None);
        Assert.False(second.More);
        Assert.Equal(new[] { "r2", "r1", "r0" }, second.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task A_long_body_is_cut_when_listed()
    {
        await Relay("long", T0, body: new string('가', NotificationHistoryRules.MaxListedBody + 500));

        var entry = Assert.Single((await _t.History.ListAsync(null, 10, CancellationToken.None)).Entries);

        Assert.Equal(NotificationHistoryRules.MaxListedBody, entry.Body!.Length);
    }

    [Fact]
    public async Task Clearing_removes_relayed_notifications_but_keeps_the_alert_log()
    {
        await Relay("엄마", T0);
        await Alert("디스크 오류", T0.AddMinutes(1));

        Assert.Equal(1, await _t.History.ClearDevicesAsync(CancellationToken.None));

        var entry = Assert.Single((await _t.History.ListAsync(null, 10, CancellationToken.None)).Entries);
        Assert.Equal(HistoryKind.Alert, entry.Kind);
    }

    [Fact]
    public async Task Pruning_drops_what_is_past_retention_and_beyond_the_cap()
    {
        await Relay("old relay", T0.AddDays(-31));
        await Alert("old alert", T0.AddDays(-31));
        for (var i = 0; i < 4; i++) await Relay($"r{i}", T0.AddMinutes(i));
        await Alert("recent alert", T0);

        await _t.History.PruneAsync(T0.AddDays(-30), maxDeviceEntries: 3, CancellationToken.None);

        var titles = (await _t.History.ListAsync(null, 10, CancellationToken.None)).Entries.Select(e => e.Title).ToList();
        Assert.Equal(new[] { "r3", "r2", "r1", "recent alert" }, titles);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 1)]
    [InlineData(1000, NotificationHistoryRules.MaxPage)]
    public void Page_size_is_clamped(int? asked, int expected) =>
        Assert.Equal(expected, NotificationHistoryRules.ClampLimit(asked));
}
