# Changelog

## Unreleased

### Changed
- Release assets are renamed to the Homebase brand: `Homebase-Setup.exe`, `Homebase-Setup.zip`,
  and `Homebase-Setup.zip.sha256` (previously `LocalOpsBot-Setup.*`). Internal names — the
  install folder, Windows service, and binaries — are unchanged.

> **Upgrade note:** the auto-updater in v0.5.1 and earlier looks for the old
> `LocalOpsBot-Setup.zip` asset, so it cannot bridge this rename. Update **once manually** by
> downloading the new `Homebase-Setup.exe`; auto-update resumes normally from then on.

## v0.5.1 — /events Fix & Event Log Failure Logging

### Fixed
- `/events` now shows the actual most-recent events. It previously shared the alert poller's
  resume bookmark, so it usually returned "No recent events" and could even hide events from
  the alert pipeline; it now reads independently, without touching poller state.

### Changed
- Windows Event Log read failures are now logged (once per failing log, plus a recovery note)
  instead of being silently swallowed.

## v0.5.0 — Event Log Bookmarks & Poll Diagnostics

### Added
- `/diagnostics` now reports the last successful Telegram poll time and the current
  consecutive-poll-failure count, so you can tell at a glance whether polling is alive.

### Changed
- Windows Event Log polling resumes from a bookmark instead of re-scanning the whole log
  every cycle — much cheaper on large logs, with identical alerting behaviour.

### Fixed
- The `/diagnostics` header now reads "Homebase" (the last stray "LocalOps" label).

## v0.4.0 — Homebase: Redesign, Onboarding & Readable Replies

### Fixed
- Command replies (`/status`, `/help`, …) no longer show raw `<b>`/`<code>` tags. The
  Telegram client now applies the configured `parse_mode` (HTML) even when the caller
  passes no options — previously only alerts/boot messages set it, so command replies
  went out unformatted.

### Added
- The tray **Settings** window is now a live dashboard: Agent service status (running /
  stopped) plus real-time uptime, CPU, RAM, network, and per-drive disk usage, refreshed
  every 3 seconds from the same collectors the bot uses.
- **First-run onboarding**: a welcome window checks connection readiness (Agent service,
  bot token, chat allowlist), sends a test Telegram message to verify end-to-end, and
  guides setup for anything missing. Shown once per user, reopenable from the tray's
  "Setup / Welcome" menu. Read-only — it never writes config, so it needs no elevation.
- **Homebase visual identity**: a Nintendo-2001 "console chrome" theme across the tray UI,
  and a proper multi-resolution app / taskbar / tray icon (replacing the runtime-drawn glyph).

### Changed
- About box shows the actual assembly version instead of a hardcoded `v1.0.0`.
- Rebranded **LocalOps Bot → Homebase** across the tray UI, installer, and docs (the
  internal service and binary names are unchanged, so existing installs upgrade cleanly).

## v0.3.0 — Streamlined Release & Hardened Updates

### Changed
- Slimmed the release to 5 clearly-named assets: `LocalOpsBot-Setup.exe` (installer),
  `LocalOpsBot-Setup.zip` (manual install), its `.sha256`, `bootstrap.ps1`, and
  `appsettings.example.json`. Removed the standalone Agent/Tray zips and duplicated
  scripts that cluttered the release page.
- Release notes now lead with a "which file do I download?" guide.
- Rewrote the README install section around the installer; corrected the runtime
  prerequisite (the build is self-contained — no separate .NET runtime needed).

### Hardened (auto-update)
- Verify the SHA-256 of every downloaded update before applying it; abort on mismatch.
- Report update download/apply failures back to the user instead of silently swallowing them.
- The apply step now logs to `update.log`, waits for the Agent to exit, verifies the new
  binary is present, and restarts the service on failure so a botched update never leaves
  the monitor down. Both Agent and Tray are updated (previously the Tray was missed).
- Pin auto-update to the `LocalOpsBot-Setup.zip` asset instead of "the first .zip".
- Stamp the version from the git tag at build time so update checks compare correctly.

### Added
- Unit tests for the updater (asset selection + checksum verification).

## v0.2.0 — Runnable + Automatic Alerts

### Fixed (runnability)
- Fix DI circular dependency (Help command) that crashed the Agent on startup
- Register missing `IHostInfoProvider`; gate toast-forwarding services behind config
- Load config from ProgramData/executable folder + `LOCALOPSBOT__` environment variables
- Resolve `ENV:` token indirection in the Telegram client
- Align `setup.ps1` config schema with the code; unify the Windows service name
- Initialize Serilog file logging (was referenced but never wired up)

### Added
- Automatic alerts for process / service / event-log / HTTP / TCP-port issues, with recovery notifications
- `AlertDispatcher` as the common alert path, wiring mute / dedup / rate-limit policy
- `/diagnostics`, `/http`, `/llm` commands

### Hardened
- De-duplicate event-log config arrays; make monitor intervals config-driven (`collectors`)
- Baseline the event-log first poll to prevent alert storms on restart
- Actually drop block-listed toast notifications (Tray drop + Agent double-check)

## v0.1.0 — Initial Release

- Telegram bot with /ping, /status, /uptime, /disk commands
- Windows process and service monitoring
- Windows Event Log watch
- Boot notifications with dedup
- Windows Toast notification forwarding with filtering and masking
- Alert policy with dedup, rate limiting, and mute
- Developer environment monitors (HTTP endpoints, TCP ports)
- SQLite persistence for command logs, alerts, and runtime state
- WPF system tray app with settings UI
- Self-contained release packages for Agent and Tray
- PowerShell installer with service recovery and Tray auto-start
- GitHub-based auto-update system
