# Changelog

## v0.18.1 — The phone finds the PC again after a restart

### Fixed
- **A paired phone no longer loses the PC after the PC restarts.** The Cloudflare Quick Tunnel
  address changes on every Agent start (an update, a reboot), and the app only learned new paths
  after it had connected, so a phone that could reach the PC only through the tunnel showed
  "PC에 닿지 않습니다" until it was paired again. The PC now pushes its new paths to paired phones
  (signed per phone, so one PC can't rewrite another's), and a retrying phone picks them up on its
  next try. Needs push notifications on in the app; without them the screen now says to re-pair
  (DECISIONS M-23).
## v0.18.0 — Update notifications, phone-viewing indicator

### Added
- **Update notification on the phone.** The Homebase app now checks for a new version in the
  background (still one download of `update.json` a day) and shows a notification once per version;
  tapping it opens the update dialog. Turning off "업데이트 확인" stops the check and clears the
  notification. If notifications are blocked, the setting offers a button to allow them.
- **"A phone is viewing this PC" in the tray.** While a phone has this PC open, the tray icon shows a
  green dot, and the tooltip and the top of the tray menu say so. The PC collects per-process data and
  window reports only during that time.

## v0.17.0 — One settings window

### Changed
- **One settings window.** "Connect phone" is now the first tab of Settings (Phone) instead of a
  separate tray menu item; the tray menu's Settings… and the dashboard's settings button open it.
  The phone tab polls the Agent only while it is in view.
- The Notifications tab now says where forwarded Windows notifications go — the phone, as an
  encrypted push — instead of Telegram, which they no longer reach by default since v0.16.0.

### Fixed
- The phone app's pairing steps, its "remove PC" dialog and the pairing link's install page sent you
  to the tray menu's "Connect phone…", which this release removes; they now point to Settings… → Phone.

## v0.16.0 — Phone push notifications, self-installing updates

### Added
- **Push notifications in the Homebase app.** PC alerts (process/service/HTTP/port down, event log,
  AI advice), the boot notice and forwarded Windows notifications arrive as phone notifications via
  UnifiedPush. Install a distributor app such as [ntfy](https://ntfy.sh/) (free, no account); the app
  turns push on by itself when a PC is paired. Payloads are end-to-end encrypted (RFC 8291), so the
  push server only relays ciphertext. Notification channels are split per PC and per source app, so
  you can mute one app on one PC in Android settings.
- `POST`/`DELETE /api/v1/push` for the app to register its push endpoint with each PC.

### Changed
- **Push first, Telegram as fallback.** Alerts and the boot notice go to the phone first; Telegram is
  used only when no phone is registered or push fails. Telegram stays for the transition (DECISIONS M-19).
- **Windows notification forwarding is on by default** and never goes to Telegram (plaintext cloud)
  unless `notificationForwarding.telegramFallback` is set. Notifications from one app are bundled for
  60 s and capped at 150 pushes a day per PC (ntfy.sh allows 250 a day per phone, shared by all PCs). Password managers and
  authenticators are always blocked — the tray now applies the configured block list too.
  An existing `"enabled": false` in your config is kept.
- The tray's "send test message" goes through the Agent (phone push first) when the Agent is reachable.
- **The startup update popup now installs on its own after 60 seconds.** The Update button counts
  down (`UPDATE NOW (60)`); pressing Later, Skip this version or closing the window stops it. The
  install still asks for administrator approval. If an unattended install of a version doesn't
  take, that version's popup waits for a click at the next launch instead of counting down again.
  A manual "Check for updates" never counts down.
- **The update download window can be minimized.** It now has a taskbar button and a minimize
  caption button; the download keeps going, and the window comes back to the front when the
  administrator prompt is next.

### Notes
- **Push needs both the PC and the app on v0.16.0.** Until both are updated, alerts keep going to
  Telegram as before. Phones without Google Play services can use the F-Droid build of ntfy; give it
  a battery-optimization exemption so notifications are not delayed.

## v0.15.0 — Phone connection without Tailscale, console look, tray settings

### Added
- **Tailscale is now optional — three connection paths from one QR code.** Besides Tailscale, the
  phone can connect directly on the same LAN (TLS with a pinned certificate; firewall rule limited
  to the local subnet, the Private network profile and the Agent executable) or from anywhere
  through a Cloudflare Tunnel (a named tunnel with your token, or a Quick Tunnel; `cloudflared` runs
  as a child process of the Agent). The app picks the path automatically — LAN on the same subnet,
  Tailscale when its VPN is on, otherwise Cloudflare — and falls back to the others if one fails.
  Settings: `mobile.lan`, `mobile.tailscale`, `mobile.cloudflare`.
- **"Connect phone…" window shows each path's status**, lets you turn paths on or off, installs a
  missing Tailscale or `cloudflared` with winget, warns when Windows has classified the LAN as
  Public (the LAN path is blocked until it is Private), and reissues the QR when the paths change.
- **Pairing QR opens with the phone's own camera.** The QR is now a link
  (`https://jeiel85.github.io/homebase/pair/#…`). With the app installed it opens the pairing screen
  pre-filled (you still tap Connect); without it, an install page shows the PC name, the time left
  and an APK download button. The connection details stay after the `#` and are never sent to the
  web server. The in-app scanner still reads the old JSON QR codes.
- **Console look shared with the tray.** Carbon instrument panel with segmented CPU/RAM arc gauges
  and 60-second history, live disk/download/upload traces, per-app CPU and RAM bars, the foreground
  window marked with an amber rail, and a status lamp per PC. Light and dark themes; work sessions
  (today: Claude Code) sit in a generic "작업" tab.

### Changed
- **Tray: status and settings are separate.** "Open Homebase" (left click) is a read-only dashboard —
  CPU/RAM/disk gauges plus a Telegram / forwarding / phone summary. The new **Settings…** window has
  one tab per area (Telegram, Notifications, Watch list, Updates); each says what it does, shows the
  current state, and marks every button that asks for administrator approval with the Windows UAC
  shield and a note on what restarts. Notification forwarding no longer elevates on a checkbox click
  (stage it, then Apply), the app list is "checked = forward", and the watch list shows unsaved
  changes. The bot token / chat ID moved from the welcome window to Settings; the welcome window is
  now a setup checklist ("Setup checklist" in the menu).
- **Tray text follows the bot's language** — Korean or English from `agent:language`, else the OS
  display language. The menu item is now "Connect phone…".

### Notes
- **The v0.14.0 app cannot read the new pairing QR.** Update the app (in-app update, or install the
  new `Homebase-Mobile.apk`) before pairing a PC on v0.15.0.

## v0.14.0 — Homebase Mobile: live multi-PC monitoring

### Added
- **Android app (`Homebase-Mobile.apk`).** Pair any number of PCs by QR code, pick one from the list,
  and watch it live: open windows (Chrome shows each window's active tab), per-app CPU / RAM / I/O /
  network (an app's window-less child processes are summed in, e.g. a terminal running an agent),
  the top 30 processes, and recently active Claude Code session titles. Refreshes every second.
- **Tailscale-only, serverless connection.** The phone talks straight to the PC's Tailscale address
  (MagicDNS name or fixed 100.x IP). The Agent listens only on that address, behind a firewall rule
  limited to 100.64.0.0/10 and the Agent executable — nothing is opened to the internet or the LAN.
- **"Connect Phone…" in the tray menu** — one admin prompt to enable, then a one-time QR code (5
  minutes) and a list of paired phones you can revoke.
- **In-app sideload updates** (ported from Markleaf): daily check of the release's `update.json`,
  SHA-256-verified download, handed to the Android installer.
- **PC update popup.** When the tray starts and a newer release exists, it now opens an update
  window with that version's release notes and three choices: **Update now**, **Skip this version**
  (not asked again until a newer release), or **Later**. The startup check also retries while the
  network is still coming up after sign-in. "Check for Updates" in the tray menu and dashboard opens
  the same window, even for a skipped version.

### Notes
- Collection runs only while a phone is watching; otherwise the feature adds no measurable load.
- Off by default (`mobile.enabled: false`). Per-process network needs the Agent running as the
  service (ETW); Claude session reading can be turned off with `mobile.claudeSessions: false`.

## v0.13.2 — Reliable boot notification

### Fixed
- **Boot notification now survives a slow network at startup.** The Agent service starts before the
  network/DNS is ready, so the single send attempt failed on nearly every boot ("No such host is
  known (api.telegram.org)") and the "PC booted" message silently never arrived. The notification now
  sends in the background and retries with backoff (5s, 10s, 20s, 40s, then every 60s, about 16
  minutes in total) without delaying Agent startup. A runtime-state database error no longer stops
  the send, and a failure to record the send time no longer triggers a duplicate message.

## v0.13.1 — Single-instance guard

### Fixed
- **Prevent duplicate instances.** The tray app and the Agent now refuse to start a second copy of
  themselves. The tray uses a session-local mutex (one tray per signed-in user), so autostart racing
  a manual launch — or a relaunch after an update — no longer stacks duplicate tray icons and pipe
  clients. The Agent uses a global mutex as a backstop against a second console-launched instance
  double-polling Telegram.

## v0.13.0 — Transport-neutral outbound routing

### Changed
- **Internal refactor: alerts and notification forwarding now route through a transport-neutral
  outbound layer.** `AlertDispatcher` and `NotificationForwardingService` no longer depend on
  `ITelegramClient` directly; they deliver through a new `IOutboundRouter` / `IOutboundChannel`
  abstraction (new `LocalOpsBot.Protocol` project + `Core/Delivery`), groundwork for future device
  channels. **Telegram output for automatic alerts is byte-for-byte unchanged.**
- **Notification forwarding message format changed.** Forwarded Windows notifications now render as
  `ℹ️ <b>App: Title</b>` + body, instead of the previous `🔔 <b>App</b>` with `Title:` / `Body:`
  labels.

## v0.12.0 — Configure monitors from the dashboard

### Added
- **Monitor configuration in the dashboard.** A new MONITORS card lets you set what the bot watches
  — processes, Windows services, HTTP endpoints, and TCP ports (one entry per line) — instead of
  hand-editing the config file. Saving applies with a single administrator prompt and restarts the
  Agent. This is what `/process`, `/services`, `/http`, and `/ports` report on, so those commands are
  now configurable without touching JSON.

## v0.11.0 — AI error interpretation + one-click Ollama

### Added
- **AI interpretation of error alerts.** With `eventLog.llmInterpret` enabled, each Windows
  event-log Error/Critical alert is run through the local LLM (Ollama), which appends a
  plain-language "what this likely means + what to check" note in your language. Best-effort — a
  disabled or offline LLM just sends the alert without the note.
- **One-click Ollama start.** The onboarding AI section now detects an installed-but-not-running
  Ollama and shows a START OLLAMA button that launches it and registers it to run at login — so it
  survives a reboot (the reason it kept showing "not detected").

### Fixed
- `/ping` printed the time as `+09:00ST`; it now shows `KST` (the format string was treating `K` as
  the offset designator).

### Security
- The bot token no longer lands in the Agent log. HttpClient request logging prints the full
  Telegram URL (token included) at Information — that category is now dropped to Warning.

## v0.10.1 — App selection is an exclude-list

### Changed
- Notification app selection is now an **exclude-list**: check the apps you do NOT want forwarded
  (e.g. Phone Link / 휴대폰과 연결, to stop a feedback loop). Everything else forwards — including
  apps that first show up later — so a new app is never silently dropped. (v0.10.0 shipped this as
  an allow-list, which would have hidden notifications from any app you hadn't explicitly checked.)

## v0.10.0 — Choose which apps forward, and only new notifications

### Added
- **App selection for notification forwarding.** The dashboard now lists the apps that have sent
  notifications; check the ones you want forwarded (none checked = forward everything). This breaks
  notification feedback loops — leave a phone-mirroring app like "Phone Link" (휴대폰과 연결)
  unchecked so its mirrored Telegram messages don't bounce straight back and pile up. Changes apply
  within a few seconds with no administrator prompt and no restart (the selection lives in a
  per-user file).

### Changed
- Forwarding no longer dumps the existing notification backlog when it starts — only notifications
  that arrive after forwarding is running are sent, one at a time.

## v0.9.1 — Notification forwarding actually delivers now

### Fixed
- **Notification forwarding could never reach Telegram.** The Agent runs as LocalSystem and created
  its notification pipe with the default security, which denied the (non-elevated) tray's connection
  — "Access to the path is denied". So the tray captured notifications correctly but they silently
  never crossed to the Agent. The pipe now grants the interactive user connect/write access, so
  forwarded notifications get through. (Forwarding shipped in v0.9.0 but had never actually worked.)

### Added
- The tray's forwarding pipeline now writes a diagnostic log to
  `C:\ProgramData\Homebase\logs\tray-forwarding.log` (notification-access status, poll activity,
  errors), so this headless feature can be diagnosed instead of failing invisibly.

## v0.9.0 — Update progress, dashboard update check, and working notification forwarding

### Added
- **Download progress.** The tray auto-update now shows a themed progress window with a live
  percentage while it downloads, then a "Verifying…" and "Installing…" phase, instead of a silent
  balloon tip. Closing the window (or Cancel) stops the download.
- **Update check from the dashboard.** The dashboard has a new UPDATES card showing the current
  version with a "Check for updates" button, so updating no longer requires the tray right-click menu.
- **Notification forwarding actually runs now.** The tray-side pipeline (Windows toast listener →
  filter/mask → named pipe → Agent → Telegram) is wired up and starts when the feature is enabled —
  previously the pieces existed but were never started, so nothing was ever forwarded. Sensitive
  content (OTP codes, passwords, tokens) is masked by default.

### Changed
- **The "Enable notification forwarding" checkbox now persists.** It reads the current setting when
  the dashboard opens, and toggling it saves to the machine config (one administrator prompt) and
  restarts the Agent and tray so the change takes effect. It used to be a cosmetic control that
  never remembered its state.
- The update flow is shared between the tray menu and the dashboard, so both behave identically.

### Fixed
- The auto-update apply step now retries file copies a few times, so a binary that stays briefly
  locked (antivirus scan, lingering handle) right after the old version exits no longer fails the
  whole update.

## v0.8.6 — Quieter, configurable event-log alerts

### Added
- Event-log alerts are now prioritized and configurable, so a busy PC no longer floods Telegram:
  - A recurring error (same source + event id) alerts at most once per `eventLog.repeatSuppressMinutes`
    (default 60 minutes); Critical events are never suppressed.
  - `eventLog.alertLevels` picks which levels actually push an alert (default Critical + Error) —
    separate from `eventLog.levels`, which still controls what is read and shown by `/events`.

### Fixed
- Event-log list settings (`levels`, `alertLevels`, `logs`, provider filters) can now be narrowed,
  not only extended — the configuration binder used to append your values onto the built-in defaults.
- Event-log alert text is localized (Korean when the bot language is Korean), matching the other alerts.

## v0.8.5 — Rounded window corners and a drop shadow

### Changed
- The tray windows now have Windows 11 rounded corners and a drop shadow (asked of DWM), so the
  custom title bar sits in a proper window frame instead of a hard-edged rectangle. Dragging and
  Aero-Snap are unchanged; on Windows 10 the corners simply stay square.

## v0.8.4 — Auto-update actually installs now

### Fixed
- "Check for Updates → Download & Install" from the tray now works. The tray runs without admin
  rights, so its update helper couldn't write to `Program Files` or restart the service and failed
  silently. It now asks for administrator approval (UAC) before applying, shows a download
  notification, brings the tray back after the update, and tells you if you decline the prompt.

> **One-time note:** this fix lives in the updater itself, so v0.8.3 and earlier can't pull it
> automatically — reinstall once with the v0.8.4 `Homebase-Setup.exe`, and in-app auto-update works
> from then on.

## v0.8.3 — Themed window title bars

### Changed
- The tray windows (dashboard, welcome, and the update dialog) now wear a custom Homebase title
  bar — a carbon caption with the app icon, the title, and themed minimize/close buttons — instead
  of the default Windows chrome, so the whole window matches the app. Dragging, Aero-Snap and the
  taskbar keep working natively via WindowChrome; the fixed-size windows show minimize + close
  (dialogs show close only).

## v0.8.2 — Consistent theming across the tray UI

### Changed
- The tray now themes the controls that were still showing as plain Windows defaults: the bot-token
  and chat-ID text fields, the notification-forwarding checkbox, the disk list text, and the
  scrollbars all follow the Homebase look now.
- The update-check prompts are a themed Homebase dialog (carbon header, signal/carbon buttons)
  instead of a bare system message box.

## v0.8.1 — Tray dashboard no longer vanishes on click

### Fixed
- Clicking the tray icon now reliably opens the dashboard and keeps it open. It used to hide itself
  the instant it lost focus — which happened immediately when opened from the tray icon, so it only
  ever flashed as an empty window and disappeared. The dashboard now stays until you close it, comes
  to the front on a tray click, and only toggles shut when it is already the active window.

## v0.8.0 — Temperature: driver-free by default, full sensors opt-in

### Changed
- Temperature monitoring now defaults to a **driver-free WMI/ACPI sensor**, so a fresh install no
  longer loads the WinRing0 kernel driver that Windows Defender flags as vulnerable
  (`VulnerableDriver:WinNT/Winring0`). Controlled by the new `temperature.source` key (`"Wmi"` by
  default). WMI exposes only coarse ACPI zone temperatures — and many desktops don't expose them at
  all — so temperatures may be blank; use the opt-in below for full sensors.
- `/diagnostics` now reports the active temperature backend and live sensor count (e.g.
  `Wmi, 0 sensors`), so an empty temperature reading is self-diagnosable.

### Added
- **Opt-in full sensors:** run `enable-temperature.ps1` as administrator to switch to
  LibreHardware (per-core CPU / GPU / board sensors). It registers a Defender exclusion for the
  driver and restarts the service; `-Disable` reverts to WMI and removes the exclusion, and
  uninstall removes it too. The script warns when Memory Integrity / the vulnerable-driver
  blocklist is active, since the driver may then be blocked from loading regardless of the
  exclusion.

## v0.7.5 — Bot replies in your language

### Added
- All bot responses are now localized, not just AI advice: command output (`/status`, `/help`,
  `/disk`, `/diagnostics`, and the rest), automatic alerts (process/service/endpoint/port down and
  recovered), the boot notification, and the active health-advice message. English is the default;
  set `agent:language` to `"ko"` for Korean, or leave it empty to follow the OS display language
  automatically.

### Changed
- `/advise` now replies in the same language as the rest of the bot: when `llmAdvisor.language` is
  empty it follows `agent:language` (or the OS display language) instead of only the OS language.

### Fixed
- The boot notification timestamp shows a literal `KST` label again instead of a mangled
  `+09:00ST` (the format string was treating `K` as a UTC-offset specifier).

## v0.7.4 — Faster advice and reply language

### Added
- Advice can reply in your language: set `llmAdvisor.language` (e.g. `"Korean"`), or leave it
  empty to follow the OS display language automatically.

### Changed
- `/advise` and automatic advice are much faster on repeat calls: the model is kept warm between
  calls (`keep_alive`) and the reply length is capped (`maxTokens`), avoiding the slow model-load
  each time (measured ~7s cold vs ~1.2s warm on `llama3.2:1b`). Both are tunable under `llmAdvisor`.

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
