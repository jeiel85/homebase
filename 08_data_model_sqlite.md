# 08. Data Model and SQLite

## 1. 저장 목적

SQLite는 다음 목적으로 사용한다.

- Telegram update offset 저장
- command execution history 저장
- alert history 저장
- notification dedup 저장
- metric samples 저장
- mute/quiet 상태 저장
- runtime state 저장

## 2. DB 위치

```text
%ProgramData%/LocalOpsBot/data/localops.db
```

## 3. Schema overview

```text
runtime_state
command_log
alert_log
metric_sample
notification_event
watch_status
```

## 4. runtime_state

Key-value store.

```sql
CREATE TABLE IF NOT EXISTS runtime_state (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
```

Keys:

| key | value |
|---|---|
| telegram.last_update_offset | long |
| agent.started_at | ISO timestamp |
| alert.muted_until | ISO timestamp |
| boot.last_notification_hash | string |
| boot.last_notification_at | ISO timestamp |

## 5. command_log

```sql
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
```

Indexes:

```sql
CREATE INDEX IF NOT EXISTS ix_command_log_received_at ON command_log(received_at);
CREATE INDEX IF NOT EXISTS ix_command_log_command ON command_log(command);
```

## 6. alert_log

```sql
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
```

Indexes:

```sql
CREATE INDEX IF NOT EXISTS ix_alert_log_created_at ON alert_log(created_at);
CREATE INDEX IF NOT EXISTS ix_alert_log_dedup_key ON alert_log(dedup_key);
CREATE INDEX IF NOT EXISTS ix_alert_log_kind ON alert_log(kind);
```

## 7. metric_sample

```sql
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
```

Retention:

- 기본 7일
- 1분 단위 sample이면 약 10,080 rows/week
- 개인 PC 수준에서는 충분히 작음

## 8. notification_event

```sql
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
```

## 9. watch_status

```sql
CREATE TABLE IF NOT EXISTS watch_status (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    watch_name TEXT NOT NULL,
    watch_type TEXT NOT NULL,
    status TEXT NOT NULL,
    status_json TEXT,
    changed_at TEXT NOT NULL
);
```

## 10. Migration strategy

초기에는 단순 schema version 방식 사용.

```sql
CREATE TABLE IF NOT EXISTS schema_version (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);
```

Migration runner:

```csharp
public interface IDatabaseMigrator
{
    Task MigrateAsync(CancellationToken ct);
}
```

Rules:

- 앱 시작 시 migration 실행
- migration 실패 시 Agent startup fail
- migration script는 idempotent하게 작성

## 11. Retention cleanup

Background job:

- 하루 1회 실행
- command_log: 30일 보관
- alert_log: 90일 보관
- metric_sample: 7일 보관
- notification_event: 14일 보관

Config:

```json
{
  "retention": {
    "commandLogDays": 30,
    "alertLogDays": 90,
    "metricSampleDays": 7,
    "notificationEventDays": 14
  }
}
```

## 12. Repository interfaces

```csharp
public interface IRuntimeStateRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}
```

```csharp
public interface ICommandLogRepository
{
    Task<long> InsertStartedAsync(CommandLogEntry entry, CancellationToken ct);
    Task MarkCompletedAsync(long id, string status, string? error, CancellationToken ct);
}
```

```csharp
public interface IAlertLogRepository
{
    Task InsertAsync(AlertLogEntry entry, CancellationToken ct);
    Task<IReadOnlyList<AlertLogEntry>> GetRecentAsync(int count, CancellationToken ct);
    Task<bool> ExistsRecentDedupKeyAsync(string dedupKey, TimeSpan window, CancellationToken ct);
}
```
