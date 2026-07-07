PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS schema_version (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);

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

CREATE INDEX IF NOT EXISTS ix_metric_sample_collected_at ON metric_sample(collected_at);

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

CREATE INDEX IF NOT EXISTS ix_notification_event_created_at ON notification_event(created_at);
CREATE INDEX IF NOT EXISTS ix_notification_event_body_hash ON notification_event(body_hash);

CREATE TABLE IF NOT EXISTS watch_status (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    watch_name TEXT NOT NULL,
    watch_type TEXT NOT NULL,
    status TEXT NOT NULL,
    status_json TEXT,
    changed_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_watch_status_name_type ON watch_status(watch_name, watch_type);
CREATE INDEX IF NOT EXISTS ix_watch_status_changed_at ON watch_status(changed_at);
