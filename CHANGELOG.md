# Changelog

## v0.7.3 — Full Homebase rebrand (service, binaries, folders)

### Changed
- Renamed to Homebase everywhere it is visible: the install folder (`C:\Program Files\Homebase`),
  the Windows service (`Homebase.Agent`), the executables (`Homebase.Agent.exe`,
  `Homebase.Tray.exe`), the data/config folder (`C:\ProgramData\Homebase`), and the bot-token
  environment variable (`HOMEBASE_TELEGRAM_TOKEN`). Reinstall with the new setup to pick it up.

## v0.7.2 — Cleaner install folder; repo renamed to homebase

### Changed
- The installer always offers the standard default folder (`C:\Program Files\LocalOpsBot`) and no
  longer pre-fills a previously-used location, so a stale install path can't carry over.
- The project repository moved to `github.com/jeiel85/homebase`; download, one-line install, and
  auto-update links now point there (GitHub redirects the old URLs).

## v0.7.1 — Uninstall fully removes the agent

### Fixed
- Uninstalling now stops and removes the Windows service, clears the bot-token environment
  variable, and unregisters tray auto-start. The service was created outside the installer's
  file tracking, so a plain uninstall used to leave it running — and still sending alerts.

### Changed
- The service and tray now run in place from the install folder instead of being copied to a
  separate hardcoded location, so there is a single, cleanly-removable install.

## v0.7.0 — Set up Telegram from the app, not the installer

### Changed
- Setup no longer asks for your Telegram bot token and chat ID. The installer just installs the
  service and tray; you enter your credentials on first run in the Homebase welcome window and
  click **Save & apply** — a single admin prompt applies them and restarts the service. Change
  them any time from the same place.
- The Agent now starts and runs idle when no token is configured yet, instead of failing at
  startup — so a fresh install stays healthy until you finish setup in the tray.

> **Upgrade note:** reinstall once with the new `Homebase-Setup.exe` to get the in-app setup flow.
> Your existing token and chat ID are preserved; you can update them from the tray afterwards.

## v0.6.0 — Local AI Health Advice (Ollama)

### Added
- `/advise` — ask a local LLM (Ollama) for plain-language health advice about your PC's current
  state (CPU/RAM/disk/network + recent alerts), e.g. "CPU has been pegged, check for a runaway
  process". Nothing is bundled: it talks to a local Ollama server configured under `llmAdvisor`
  (endpoint/model), and falls back to a friendly setup hint when no server is reachable.
- Temperature monitoring: CPU/GPU/board sensor readings now show in `/status` and feed `/advise`,
  so an overheating component is something the advisor can actually flag. Read via
  LibreHardwareMonitor inside the Agent (the elevated service; the tray can't read sensors);
  machines that expose no sensors are handled gracefully, and implausible sentinel readings
  (e.g. a `255°C` "not ready" value) are filtered out. Set `temperature.enabled` to `false` to
  skip collection entirely — for instance if antivirus flags the kernel driver.
- Active health advice (opt-in): a background monitor watches CPU, memory, disk, and temperature,
  and when a threshold stays breached it asks the local LLM for advice and sends it to Telegram —
  so you hear about a hot or overloaded PC without running `/advise` yourself. Off by default;
  enable and tune it under `advisorAlerts` (per-metric thresholds, poll interval, a sustained-breach
  count, and a cooldown that caps how often advice is sent). Advice flows through the existing
  mute/dedup/rate-limit path, and the LLM is only called once a breach persists past the cooldown.
- Ollama setup onboarding: the Tray welcome window now checks whether the local Ollama server is
  running and whether the configured model is pulled, and guides you accordingly — a "Get Ollama"
  link when it isn't reachable, or a one-click "Run pull" for the missing model — so `/advise` and
  automatic advice are easy to turn on. Read-only and non-elevated, like the rest of onboarding.

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
