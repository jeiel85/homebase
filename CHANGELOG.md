# Changelog

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
