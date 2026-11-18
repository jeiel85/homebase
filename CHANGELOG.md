# Changelog

## v0.42.1 — Security fixes from an audit

### Security
- PC: **an update is installed only after it was verified, and only what was verified.** A release
  without its published SHA-256 (`Homebase-Setup.zip.sha256`) used to install unchecked; it is now
  refused. And the administrator step no longer runs a script from, or extracts the package in, your
  user `%TEMP%` (which any program running as you can change between the check and the install): the
  script is passed inline, the package is copied into a folder only SYSTEM and Administrators can open,
  its SHA-256 is checked again there, and it is extracted there. Windows PowerShell is started by its
  full path.
- PC: **saving settings applies exactly what Homebase wrote.** The settings were staged in `%TEMP%` and
  copied by an administrator script that was also kept there, so a program running as you could change
  either in the meantime — for instance to make the service (SYSTEM) start a program of its choosing as
  cloudflared. The administrator step now checks the staged settings' SHA-256, writes those bytes as a
  new file, and runs from its command line. Telegram setup also starts Windows PowerShell by full path.
- PC: **standard users can no longer create files in `C:\ProgramData\Homebase`**, where the service reads
  its settings (only read; the tray still writes its own log in `logs\`). The service applies this when it
  starts and removes links planted there; the installer applies it too, moves aside a settings file that
  no administrator created, and limits an install folder chosen outside Program Files to
  administrators (users read and run).
- PC: **a linked PC can no longer answer Claude Code approvals or register for push notifications** on
  another PC. Only phones did either; a PC's token doing it could have received approval prompts and
  answered "Allow" for you.
- PC: **linking refuses a PC that answers with another PC's id.** Before, an address handed over in a
  group introduction could pose as an already linked PC and have its real link dropped.

## v0.42.0 — The Play build tells you when a new version is out

### Added
- Phone (Google Play build): **a new version on Google Play is announced.** The app asks Google Play whether
  a newer version is out for you (twice a day, when you open the app after six hours, and when you pull the
  PC list down). When one is, a banner above the PC list and a notification (once per version) say
  "새 버전 X이 있습니다" (Version X is available); tapping either opens Homebase's page in the Play Store,
  which installs it. Turn it off in 설정 → 업데이트 → "새 버전 알림" (New version notifications)
  (DECISIONS M-57). The Play build still downloads and installs nothing itself.

### Known limits
- Google Play can report a new version a few hours after it is published, and if the Play Store has already
  updated the app on its own, the notification may arrive after the fact (it clears the next time the app
  opens). A copy not installed from Google Play (for example a sideloaded test build) only says that it can
  check for updates when installed from the Play Store.

## v0.41.0 — The phone app updates itself while you're not using it

### Added
- Phone (GitHub build): **new versions install themselves while the app is out of sight.** When the daily
  check finds a new version and no Homebase screen is open, the app downloads it, checks its SHA-256, and
  installs it without asking (Android 12+); a quiet notification then says "Homebase X(으)로
  업데이트했습니다" (Updated to X). Turn it off in 설정 → "새 버전 자동 설치" (Install new versions
  automatically) to keep the previous behavior: a notification, and installing when you tap (DECISIONS M-56).
  Updates you start from the open app still go through Android's install screen, as before.

### Known limits
- **Confirm once more.** Installing this version, and — if you installed the app from a browser or file
  manager — the first update after it, still ask for confirmation; after that, updates are silent. When
  Android asks, the app does not pop up an install screen on its own: you get the usual "new version"
  notification instead.
- Android 11 and older always ask; the app keeps notifying there. The Play build is updated by Google Play
  and has none of this.

## v0.40.0 — Hardening: updates, AI-session alerts, approvals and the phone's notification stack

### Fixed
- **An install that started on its own while you were away no longer leaves a notice that blocks updates.**
  When the startup countdown's install was not approved at the administrator prompt or failed, a notice
  waited for someone to close it, and until then the daily check and "업데이트 확인" (Check for updates) only
  answered that an update was already in progress. The notice is no longer shown for these installs; the next
  update window says where the install stopped, as before (DECISIONS M-55). Notices for installs you start
  yourself are unchanged.
- Homebase notices now open over the window that asked (the Homebase window, the update window) and stay
  above it, instead of possibly behind it when the tray had lost the foreground.
- **Claude Code permission prompts no longer wait 10 minutes when no phone takes them.** If every phone has
  "AI 세션 권한 요청" (AI session permission requests) turned off, the prompt is asked on the PC at once
  (DECISIONS M-54).
- A permission prompt whose tray disconnected (closed or restarted) is withdrawn from the phones right away
  instead of at its 10-minute deadline.
- **Uninstalling Homebase removes its Claude Code hook** from each user's `.claude\settings.json`, leaving
  the rest of the file (the previous file is kept as `settings.json.homebase.bak`).
- Phone: **the Homebase stack's summary stays true after you clear notifications.** Its "새 알림 N건"
  (N new notifications) count and lines are rewritten from what is left when one is swiped away or opened.
- Phone: when the stack's limits (8 per source, 24 in all) take older notifications out of the shade, the
  summary now says so — "이전 알림 N건은 알림 센터에 있습니다" (N earlier notifications are in the notification
  center).
- Phone: bundles left in the shade by v0.38 and earlier move into the single Homebase stack when the app
  opens.
- Phone: a notification relayed from another device no longer repeats the app's name in its title
  ("카카오톡: 김철수" → "김철수"); the line above already names the device and the app.

### Changed
- **An AI session found already waiting when the tray starts is announced once** if it started waiting in
  the last 10 minutes — for example while an update restarted the tray. Older waits are not replayed
  (DECISIONS M-53).
- New Codex sessions appear in the phone's work tab and alerts within a few seconds instead of up to
  30 seconds later.
- Phone: a relayed device's badge colour no longer matches one of your paired PCs' colours (unless all eight
  are taken), and the notification center keeps the colour each notification had when it arrived — also for
  PCs removed since.

### Diagnostics
- tray.log notes, once, when Claude Code or Codex session files stop having the fields Homebase reads (after
  an update of those tools changed their format), and when an update result notice was shown and closed.
- Phone: notification badges are drawn once per PC instead of for every notification.
- The tray now has automated tests (update flow windows), run in CI and before every release.

### Known limits
- Android 16 still gathers stand-alone Homebase notifications (permission prompts, kinds set to "하나씩")
  under one more "Homebase" entry of its own; giving each its own group does not change that (checked on the
  emulator), so it is left to Android.

## v0.39.1 — The update window no longer gets stuck at 100%

### Fixed
- **The update window no longer stays stuck on "설치하는 중… 관리자 승인 창에서 허용하세요" (Installing… approve the
  administrator prompt) at 100%.** When the administrator prompt was declined or closed unanswered, or the
  download or installer launch failed, the result notice opened *behind* the progress window and locked it:
  the window stayed at 100% with its Cancel and X buttons dead, and the notice that would have closed it could
  not be seen. The progress window now closes first, and the notice ("업데이트 취소" and the like) appears in its
  place.
- Homebase notices (update results and other confirmations) now have their own taskbar button, so one that opens
  behind another window can still be found and answered.

## v0.39.0 — Phone notifications stack under one Homebase entry

### Changed
- **One Homebase stack in the phone's notification shade.** Bundled notifications from every PC, app and
  relayed device now join a single Android group, so the shade shows one "Homebase" entry instead of one
  per PC and app (plus the loose ones Android folded on its own). Its header names who sent them
  ("작업실, 거실 외 1"), expanding it shows each notification with its PC badge and "PC · app" line, and
  tapping the summary opens the notification center. Each source still keeps its newest 8 in the shade,
  and the stack its newest 24 in all; everything stays in the notification center (DECISIONS M-52).
- The stack sits in the alerting section while it holds an alerting notification and moves to the silent
  section once those are dismissed, whatever order quiet and urgent notifications arrived in.

### Unchanged
- Claude Code permission prompts (허용/거부), kinds or apps set to "하나씩" (bundling off), and the tray's
  test notification still appear on their own. Android notification channels per PC and app, and the
  notification center's per-source stacks, are as before.

### Known limits
- Bundles left in the shade from the previous version stay until they are opened or cleared.
- When several stand-alone notifications pile up, Android may fold them into a second Homebase entry.

## v0.38.1 — The Notifications tab keeps its width

### Fixed
- The unread count on the Homebase window's **알림 센터 (Notifications)** tab is now a small amber badge on the
  tab's top-right corner, like the phone app's bell, instead of part of the label ("알림 센터 · 3"). Opening the
  tab (which marks everything read) no longer shortens it and moves the **설정 (Settings)** tab sideways. Screen
  readers still hear the count ("알림 센터, 새 알림 3건").

## v0.38.0 — Device aliases for the whole group

### Added
- **Name your devices on the PC.** On the tray dashboard, click the pencil next to a device's name (or
  press F2, or right-click its tile in the list) and type an alias — "작업실 PC", "내 폰". The alias
  replaces the name the device reports (such as `DESKTOP-5BP128L` or the phone's Android name) everywhere
  the tray shows it: the device list and header, "via …" lines, the notification center (senders and
  filters, past notifications included), the balloons of relayed notifications, and Settings › Phone.
  The reported name stays visible in small print. Leave the field empty, or choose "원래 이름"
  (Original name), to go back (DECISIONS M-50).
- **Aliases are shared across the device group.** An alias set on any phone or PC of the group shows on
  every member: a PC named on the phone shows under that name on the other phones and in every PC's tray,
  and a phone or PC named in one tray shows under it in the others and (for PCs) on the phones. The name
  set last wins. Other PCs get it at once (a PC that was off, within 2 minutes of coming back); phones when
  their PC list refreshes (pull to refresh, or within 10 minutes while the app is open). Aliases already
  set on a phone are shared the first time it syncs, and any alias set since wins over them
  (DECISIONS M-51).

### Changed
- The phone app's alias dialog now says the name is shown on every phone and PC of the group.

### Known limits
- Phones and PCs on v0.37.0 or older neither send nor receive aliases.
- A phone that renames itself (its Android device name) loses its alias until it is named again; a PC
  renamed in Windows keeps its alias.
- The phone app shows aliases of PCs only; phone aliases show in PC trays.
- Balloons show the alias only when the Agent is v0.38.0 or later.

## v0.37.0 — Notifications and Settings are tabs of the Homebase window

### Changed
- **One window instead of three.** The tray window now has three tabs in its header — **기기 (Devices)**,
  **알림 센터 (Notifications)** and **설정 (Settings)** — instead of opening the notification center and Settings
  as windows of their own (DECISIONS M-49). The tray menu's "알림 센터" and "설정…", a notification balloon and the
  setup checklist's Telegram button open the same window on that tab. The unread count sits on the Notifications tab.
  - Every tab has a list down the left like the device list. Settings lists its areas — Phone, Telegram, Notification
    forwarding, Watch list, Updates — each with a line on what it holds. The notification center lists its sources —
    everything, this PC's alerts and each device that sent something, with how many — and "모두 지우기" sits under them.
  - The chips on this PC's page (notification forwarding, phone connection, Telegram) open the Settings area they
    belong to instead of always the Phone area.
  - Switching tabs keeps what you typed in Settings; hiding the window drops unsaved input, the bot token included,
    as closing the Settings window did. A pairing QR that ran out while you were elsewhere is replaced when the Phone
    area comes back into view.
  - Nothing is read for a tab until it is in view, and the device page stops asking for live views while another tab
    is in front.
- English UI: the Settings area for notification forwarding is now called "Notification forwarding" (it was
  "Notifications", which is now the notification center's tab).


## v0.36.0 — Answer Claude Code's permission prompts from your phone

### Added
- **Allow / Deny from the phone.** Turn on Settings › Phone › "AI 세션 승인" on the PC, and while
  you are away from it (no keyboard or mouse for a minute), Claude Code's permission prompts — run a
  command, edit a file, fetch a page — reach your phone as a notification with the command or file
  it is about and **허용 (Allow)** / **거부 (Deny)** buttons. The buttons need the phone unlocked. Come
  back to the PC, or leave it unanswered for 10 minutes, and Claude Code simply asks on the PC as
  usual. Off by default; turning it on adds one hook to your `~/.claude/settings.json` (the previous
  file is kept as `settings.json.homebase.bak`) and turning it off removes just that hook
  (DECISIONS M-48).
- A new notification kind, "AI 세션 권한 요청" (pops up by default), can be reprioritised or turned off
  like the others. While a prompt was just sent with buttons, the plain "waiting for input" alert for
  that session is skipped.

## v0.35.0 — Device group: connect once, connected everywhere

### Added
- **Connecting a device means joining its group.** Pair a phone with one PC or link two PCs, and within a few
  minutes every PC and phone of the group is connected to every other — no more linking PCs pair by pair in
  Settings (DECISIONS M-47).
  - A phone paired with PC1 and PC2 links the two PCs to each other, so each one's dashboard shows the other live.
  - A PC linked to one member links to the rest of the group's PCs, and links are made both ways.
  - A phone joins the group's PCs it is missing and adds them to its PC list.
- Members introduce each other with fresh one-time pairing codes carried over connections that are already
  paired; an introduction never cancels a QR code shown on screen.

### Changed
- **Removing a device removes it from the whole group.** Revoking a phone or unlinking a PC in the tray's Settings
  disconnects it on every PC of the group, and a PC that was off at the time follows when it comes back (kept for
  a week). Pairing it again by hand brings it back. Deleting a PC in the phone app only stops that phone from
  rejoining it.

## v0.34.0 — See your devices from the PC

### Added
- **The tray dashboard is now a device hub.** Click the tray icon: every device is listed on the left — this PC,
  the PCs linked either way, the phones (and the devices your linked PCs see) — and the one you pick fills the
  right side (DECISIONS M-46).
  - **PCs, live, at the phone app's level**: CPU, memory and main-GPU gauges with the last 60 seconds, disk I/O,
    network in/out, ping and temperature, drives, every GPU's VRAM, temperature, power and clock, and the apps,
    processes and work (AI sessions, editors, documents, browser tabs) tabs, sortable by CPU, memory or name.
    App rows open to show their other windows and child processes.
  - Both PCs of a link see each other, whichever side made the link: the PC you linked to opens its live view
    for you, and a PC that linked to you shares its live view while you look at it (it needs v0.34.0 too; an
    older one shows its last CPU, memory and uptime with a note). Views close 10 seconds after you hide the
    window, so nothing is collected meanwhile.
  - **Phones**: their last report — battery level, temperature, health, charger and time to full, memory and
    storage, network, signal bars and Wi-Fi speed, Android version, power saving, heat, Do Not Disturb, whether
    the phone forwards its notifications — and, from this PC, whether it takes this PC's push (and which kinds
    it turned off) and when it was paired.
  - This PC's page shows notification forwarding, phone connection and Telegram as chips (click for Settings)
    and the Task Manager autostart warning. Without the phone connection it falls back to CPU, memory, drives
    and network read by the tray itself.
- App: phones report memory, storage, uptime, Android version, battery temperature, health, charger and time to
  full, power saving, heat, signal, Wi-Fi link speed, notification forwarding and Do Not Disturb to their PCs.
  Nothing changes on the phone's screens.

### Changed
- The dashboard window is resizable (default 980×680) and remembers its size and the device you last looked at.
  It uses the phone app's light console look and Segoe UI with Malgun Gothic for Korean.
- The tray's "viewing this PC" indicator now says "another device" (a linked PC can watch too), and this PC's
  own dashboard does not light it.
- Phones send their state early only when something you would notice changes (battery, charging, network,
  charger, power saving, heat, forwarding, Do Not Disturb, signal); the rest rides along once a minute.

## v0.33.0 — AI coding sessions in detail, and alerts when they need you

### Added
- **AI session detail on the work tab.** Each Claude Code and Codex session now shows what it is
  doing — 작업 중 (working) / 입력 대기 (waiting for you, highlighted and listed first) / 쉬는 중 (idle)
  and for how long — what it is waiting for, the model (and Codex's reasoning effort), how much
  context is in use (a bar for Codex), its permission mode, the last tool it used, the pull request
  it is on and Codex's total tokens. A session left waiting for approval stays listed even if its
  transcript has been quiet for a long time. No prompt, reply or tool argument leaves the PC
  (DECISIONS M-44).
- **"Claude에서 열기" (Open in Claude).** A Claude Code session's card opens that very session on
  claude.ai / the Claude app (Remote Control), where you can type to it from the phone.
- **Alerts when an AI session needs you.** While you are away from the PC (no keyboard or mouse for
  a minute), the phone is notified when a Claude Code session starts waiting for your approval or
  answer, and when Claude Code or Codex finishes a turn that took more than a minute. Two new
  notification kinds, "AI 세션 입력 대기" (pops up by default) and "AI 세션 작업 끝" (rings by default),
  can be turned off or reprioritised like the others (DECISIONS M-45).
## v0.32.0 — Hardening

Collected fixes from the hardening issues opened since v0.13.0 and bugs #2 and #5 (DECISIONS M-39–M-43).

### Security
- **The tray's notification pipe is no longer open to every local account.** `Homebase.NotificationPipe`
  admitted all Authenticated Users, so another account on the PC could inject notifications that the
  service forwarded to your phone. It now admits only SYSTEM, Administrators and the tray's user (#5).
- **`/api/v1/info` through the Cloudflare Tunnel no longer tells anonymous callers the PC name and OS
  version.** Paired phones still get the full answer (M-39).
- The installer locks the data folder to SYSTEM and Administrators before the service first starts.
- A copied pairing code or link is kept out of Windows clipboard history and cloud sync, and is cleared
  from the clipboard when it expires (only if the clipboard still holds it).

### Fixed
- **Alerts raised during a short network outage are sent once the network is back** (retried for up to
  30 minutes) instead of being dropped (#2, M-41).
- **A repeated alert is no longer held back until UTC midnight.** Duplicate suppression compared dates as
  text, so after one alert the same alert (for example a process that went down again) stayed silent for
  the rest of the UTC day instead of for its 10-minute window.
- Windows notifications still being bundled when the service stops (restart or update) are sent, not lost.
- The boot notice is retried when the network changes, for up to 6 hours, when the network comes up
  after the first 16 minutes of retries.
- The service's database access is serialized; several services used one SQLite connection at once.
- A PC address list too long for one push is cut to fit and sent, instead of failing every 5 minutes.
- A corrupt `hubs.json` is moved aside as `hubs.json.corrupt-*` instead of being overwritten.
- A duplicate outbound channel no longer stops the service from starting.
- Tray: `tray-forwarding.log` no longer grows without limit (tray logs roll at 5 MB and keep 3 files),
  and the forwarding "alive" line is written once per quiet hour (was about 2,000 lines a day).
- Tray: Settings and other windows fit short screens (such as 1366×768 at 125%), and dialogs that grow
  stay inside the screen.
- Tray: "업데이트 확인" while an update window is open brings that window forward instead of doing nothing,
  and an update found while the first-run checklist is open is offered when it closes.
- Tray: the Phone tab stops polling the service while Settings is minimized.
- Tray: a reply the service doesn't understand, or an error reply, no longer drops the tray's connection
  or blanks the device list; the service answers unknown requests with "unsupported".
- App: choosing 어둡게 or 밝게 no longer flashes the other background at start (the system splash follows
  it on Android 13+).
- App: a PC whose clock went backwards is heard again: its address pushes are accepted again once 10
  minutes have passed on the phone (M-40).
- App: if Android restarts the app after "install unknown apps" is granted, the update download resumes.
- App: the manual update check says when a new version needs a newer Android, instead of "최신 버전입니다".
- App: the notification permission is not asked again after Android closes the app in the background.
- App: the notification center says once when earlier history could not be read and was reset.
- App: Back from a PC opened with "PC 열기" in the notification center returns to the notification center.
- App: a PC renamed in Windows shows its new name on its card, its screen and its Android notification
  group (aliases are kept).
- App: push handling and notification history writes no longer run on the main thread.

### Added
- `mobile.nvidiaSmi` (default `true`): set it to `false` so a laptop's NVIDIA GPU can power down while a
  phone watches; the GPU card then shows the values Windows reports.
- Tray: checks for updates once a day while it runs (the menu item changes; no popup mid-work).
- Tray: the dashboard and Settings › 알림 warn when Homebase is turned off in Task Manager › Startup apps,
  which silently stops notification forwarding after the next sign-in.
- Tray: the update window says how the last unattended (countdown) install of that version ended: stopped
  at the administrator prompt, failed, or did not take.
- Tray: `C:\ProgramData\Homebase\logs\tray.log` records update checks and choices and onboarding state.
- App: Settings › 업데이트 shows when the app last checked, with a hint when it hasn't for days.
- App: an install not signed with the release key (such as a debug build) explains how to switch to the
  release APK instead of failing at install time.
- App: expanded app cards say "창 N개 더" when an app has more than 8 windows.

### Changed
- An ntfy 507 answer is reported as "phone not subscribed": that phone is paused for 5 minutes and the
  cause is logged once. Telegram errors are classified so only passing failures are retried.
- Transient Telegram polling errors, repeated forwarding failures and `nvidia-smi` failures are logged
  once as a one-line warning instead of a stack trace or a line every few seconds.
- PC-to-PC inbox long polls are capped at 16 at a time, so phones always get a connection (M-42).
- Linking with a connection code stops searching as soon as the PC is found on the tailnet.
- The PC's 40-app list is chosen by foreground, busy (5 % CPU or more) and memory instead of the last
  second's CPU, so apps no longer drop in and out every second.
- Network totals also leave out adapters Windows marks as software, not only known virtual adapter names.
- App: the Processes tab keeps a steady order (recent CPU average, re-ranked every 10 seconds), and app
  and process order is kept when a PC's screen is reopened within 2 minutes.
- App: pull to refresh stops spinning after 4 seconds (slow PCs still update when they answer), and
  pulling again within a minute reuses the last update check instead of asking GitHub again.
- App: the phone reports its state to each PC when it changes or once a minute (was every 15 seconds).
- App: after a minute with no ping reply, the ping cell shows a muted "ICMP 차단 가능" hint instead of a
  red 100 % loss.
- App: the "받을 알림" description says that kinds turned off on every phone don't go to Telegram either.
- App: the Play build no longer carries the GitHub update address, and Play bundles include native debug
  symbols.
- Release: the Windows tests run in CI on every pull request and before each release, the release checks
  that the pairing-link file on jeiel85.github.io lists the release certificate, and pull requests must add
  a CHANGELOG entry under an unreleased version.

## v0.31.0 — Tell PCs apart in notifications

### Changed
- **Each PC has a colour badge.** Every paired PC gets its own colour and the first letter of its
  name (or alias), shown as a round badge on its Android notifications, before its name on the PC
  list and on its notification center cards, so a notification can be matched to its PC at a glance.
  Colours are picked so two PCs don't share one (up to 8 PCs) and stay put when PCs are added,
  re-paired or removed. Notifications relayed from your other devices carry that device's badge
  (DECISIONS M-38).
- **Notifications read sender first.** The small header line now names the PC (or the device a
  relayed notification came from) and then the app or alert kind — "작업실 · 카카오톡",
  "작업실 · 프로세스 감시" — and the bold title is just what the notification says ("홍길동" rather than
  "카카오톡: 홍길동"). Bundles name the PC in the header and the app in the title.
- In the notification center, a relayed notification's header names the device first and the PC it
  came through last ("Galaxy S24 · 카카오톡 · 작업실 경유").

### Known limits
- When Android itself folds four or more Homebase notifications into one collapsed entry, the
  collapsed lines show only titles and text; expand it to see each one's badge. Bundles from one app
  on one PC keep the PC in their header.

## v0.30.0 — Bundled notifications and notification priorities

### Added
- **Notification priorities on the phone.** Settings → 알림 → "알림 우선순위" (Notification priorities)
  sets how each kind of notification (Windows app notifications, watch alerts, event log, PC health
  advice, boot, other devices' notifications) appears: 긴급 (pops up, sound, vibration), 보통 (sound
  and vibration in the shade), 조용히 (silent, in the shade) or 센터에만 (only in the notification
  center, not in the shade), or popup, sound and vibration switched one by one. Apps that sent Windows
  or relayed notifications are listed too, so one app can be set apart from its kind (for example,
  messenger pops up, everything else stays quiet). Choices apply from the next notification and stay
  on the phone; the PC is not involved (DECISIONS M-37).
- **Bundling.** With "같은 출처끼리 묶기" (Bundle same source, on by default), notifications from one
  app (or one alert kind on one PC) fold into one group in Android's notification shade, keeping the
  newest 8 there; the notification center keeps them all. Tapping the group opens the notification
  center. In the notification center itself, a day's notifications from one source fold into one card
  that lists the newest three lines and opens to show all of them.

### Changed
- **Other devices' notifications no longer pop up by default.** Notifications relayed from your other
  phones and PCs were shown like PC alerts (heads-up with sound), and each one was posted separately,
  so a busy group chat filled the shade with expanded entries. They now ring in the shade without
  popping up, bundled per device and app. PC alerts still pop up. Change either in the new settings.
- Notification center cards are shorter when closed (one title line, two body lines), and the title
  no longer repeats the app the header already names.
- Changing a priority creates a new Android notification channel for that app and PC; sound or other
  details changed in Android's own settings for the old channel start over. An app or PC turned off in
  Android's settings stays off.

## v0.29.0 — GPU, drives, temperature and ping on the phone

### Added
- **GPUs on the PC screen of the phone app.** Each GPU gets a card with its load (meter and the last
  60 seconds), the video memory in use (for an integrated GPU, the system memory it uses), and its
  temperature, power, fan and clock where the PC can read them. Any GPU maker works: the values come
  from Windows itself (the same source as Task Manager), without a kernel driver. On NVIDIA cards the
  Agent also runs `nvidia-smi` while a phone is watching, for power in watts and clocks. On a laptop
  this keeps the NVIDIA GPU awake while you watch (DECISIONS M-34).
- **Drives, internet ping and temperature.** Fixed drives with a fill meter and the space left; a ping
  to 1.1.1.1 every 2 seconds with the last result, the one-minute average and the loss (set
  `mobile.pingHost` to another host, or to `""` to turn pinging off); the CPU temperature (or, with the
  default sensors, the board temperature) with its history; and the CPU model name.
  Like the rest of the live view, all of this is read only while a phone is watching.

### Fixed
- **The network speed on the phone no longer counts traffic twice.** Tailscale, VPN, Hyper-V/WSL and
  VMware adapters were added to the physical Wi-Fi or Ethernet adapter that carries the same traffic,
  so that traffic was counted twice (watching over Tailscale counted the live view's own traffic on
  both adapters). Only physical Wi-Fi and Ethernet adapters count now (DECISIONS M-35).

## v0.28.0 — PC aliases

### Added
- **Give each PC your own name on the phone.** In the phone app, a PC card's menu has "별칭 지정"
  (Set alias). The alias replaces the Windows computer name (such as `DESKTOP-V0JCEPJ`) on the PC
  card, the PC's live screen, its push notifications (and its group in Android's notification
  settings), the notification center and the "via" line of other devices. The computer name stays
  visible in small print under it. Leave the field empty, or tap "원래 이름" (Original name), to go
  back. Aliases live on this phone only: other phones and the PC tray still show the computer name
  (DECISIONS M-36).

## v0.27.0 — Separate Google Play build

### Added
- **A Google Play build of the phone app.** The app is now built per distribution channel: the GitHub
  build (`Homebase-Mobile.apk`) is unchanged and keeps updating itself from GitHub Releases; the Play
  build is updated by Google Play and has no in-app updater, no "install unknown apps" permission and
  no "업데이트" section in settings. The two are signed with different keys, so one cannot be installed
  over the other: to switch, uninstall the app, install the other one and pair your PCs again
  (DECISIONS M-33, `docs/PLAY_RELEASE.md`).

### Changed
- The phone app now targets Android 16 (API 36), which Google Play requires for new apps.

## v0.26.0 — Notification center on the PC

### Added
- **Notification center in the tray.** The tray menu has "알림 센터" (Notification center) with an unread
  count, the dashboard has the same button, and clicking a relayed notification's balloon opens it. It
  lists what your phones and linked PCs sent to this PC, plus this PC's own alerts (event log, watchdog,
  dev server), grouped by day, with a filter per source. Click an entry for its full text. "모두 지우기"
  (Clear all) removes the relayed notifications; this PC's alerts stay because they also serve
  diagnostics. The Agent keeps them for 30 days (relayed ones: at most 500), so nothing is missed while
  nobody is logged on. This PC's own Windows notifications are not added: Windows' notification center
  already has them (DECISIONS M-32).

### Changed
- The Agent's data folder (`C:\ProgramData\Homebase\data`) is now readable only by SYSTEM and
  Administrators. It was readable by every local user, and it now holds relayed notification text.
- This PC's alert log is trimmed to the last 30 days (it used to grow without limit).

## v0.25.1 — Notification badge fix

### Fixed
- **The unread badge on the phone app's bell no longer looks cut off.** The badge was drawn inside
  the bell button, which clips to a circle, so its top-right corner was shaved off along the arc (on
  "99+" the "+" too). It is now drawn on top of the button: a round badge for 1–9, a pill for
  10–99 and "99+", with the number centred. Tapping the badge still opens the notification center.

## v0.25.0 — Notification center

### Added
- **Notification center in the phone app.** A bell at the top of the PC list shows how many pushes
  arrived since you last looked; tap it to see every push this phone received, grouped by day, with
  filters by kind (Windows app notifications, watch alerts, event log…) and by PC. Tap an entry for
  the full text, when the PC sent it, and buttons to open that PC or delete the entry. Pushes stay
  there after you clear them from Android's notification shade, and also when Android notifications
  were turned off. The history is kept on the phone only, encrypted, for 30 days or 500 entries;
  the trash icon clears it, and removing a PC from the app removes its entries (DECISIONS M-31).

### Fixed
- README: the phone app's add button is "기기 추가", and the push switch lives in the app's settings.

## v0.24.0 — Link PCs with a 24-word code

### Added
- **Link two PCs with a 24-word code.** Settings › Phone has a new "코드 복사" (COPY CODE) button next to
  "링크 복사": it copies the pairing as 24 words (the BIP39 list, like Brave Sync) and shows them
  numbered. Paste them into "다른 PC 연결" on the other PC, which finds this one on the same network
  or tailnet by itself and links (DECISIONS M-30). The link still works too, and is the way when the
  PCs only reach each other through Cloudflare. A code lasts as long as the QR: 5 minutes, one use.

### Changed
- The phone app's add button and screen now say "기기 추가" (add device) instead of "PC 추가", and the
  screen explains how other phones and other PCs join.

## v0.23.0 — Your PCs and phones see each other

### Added
- **Every device sees the others.** Each PC now keeps a list of everything paired with it — phones
  and other PCs — with their state: battery and network for phones, CPU, memory and uptime for PCs,
  and whether each one is online. The phone app shows them under "다른 기기" below your PC list;
  the PC shows them in a new DEVICES section of the dashboard. Phones report about every 30 minutes
  in the background and whenever the app checks a PC (DECISIONS M-27).
- **Link two PCs.** Settings › Phone on one PC has a new "COPY LINK" button; paste that link into
  "Link another PC" on the other PC. The two PCs then see each other's state and get each other's
  Windows notifications, and a phone paired with either PC sees both (DECISIONS M-29).
- **Notifications travel between devices.** A notification that reaches one device is shown on the
  others: a PC's Windows notifications reach the PCs linked to it, and — if you turn on
  Settings › 다른 기기 › "이 휴대폰 알림을 PC로 보내기" and allow Android's notification access —
  a phone's notifications appear on its PCs and on the other phones paired with them. Relayed
  notifications go one step only, never loop back, and a phone paired with two PCs gets each once.
  Authenticators, password managers and phone-mirroring apps are left out by default, and any app
  can be left out after it shows up (DECISIONS M-28). The phone can mute these with the new
  "다른 기기 알림" switch under 받을 알림.
- **The work tab shows more than Claude Code.** It now lists Codex sessions (title and folder from
  Codex's own session index), the documents open in Word, Excel, PowerPoint, OneNote, Hangul,
  Acrobat and similar apps, code editors and IDEs with their project, and the active tab of each
  browser window — with the window you're looking at first (DECISIONS M-26).

### Changed
- The "Windows 알림만" shortcut under 받을 알림 is now "앱 알림만" and keeps notifications from your
  other devices on too.

### Notes
- Both sides need this version: an older PC doesn't list devices or relay notifications, and an
  older phone app shows only Claude sessions in the work tab.
- Relayed phone notifications share the push relay's daily allowance (250 a day per phone on
  ntfy.sh) and the PC's forwarding limit (150 a day).

## v0.22.0 — A steady app list

### Changed
- **The 앱 tab says how it is sorted, and you can choose.** Above the cards: "정렬 CPU · 메모리 ·
  이름" with a line saying what the order means. CPU (default) and 메모리 put the app in use first,
  then the heaviest; 이름 is alphabetical and never moves. The choice is remembered.
- **The list no longer reshuffles every second.** The PC sends the apps ranked by the last second's
  CPU, so idle apps traded places on noise. The phone now re-ranks at most every 10 seconds, by a
  smoothed CPU average in bands (idle / 1 / 5 / 15 / 40 %) or by memory in ×1.5 steps, and an app
  must be clearly past a band edge to move; apps in the same band keep their places. New apps still
  appear where they rank, and closed apps leave, right away. The numbers on the cards stay live.
- **App cards keep one height.** Collapsed, each card always has the same rows: name, one window
  title (the one in use), CPU/RAM bars, and a footer with disk (and per-app network where the PC
  measures it) plus window and child-process counts. Before, the disk/network line vanished at zero
  and a second title came and went, so cards — and everything below them — jumped.

## v0.21.0 — Choose which notifications reach the phone

### Added
- **Choose what kinds of PC notification you get on the phone.** Settings → 알림 → "받을 알림" has a
  switch for each kind — Windows app notifications, watch alerts (process, service, website, port),
  event log, PC health advice, boot — and two shortcuts, "모두 받기" and "Windows 알림만". The choice
  goes to every paired PC, which then doesn't send what's off, so muted notifications no longer use
  the push relay's daily allowance. Muting one app or one PC still works in Android's notification
  settings. A kind turned off on every phone is not sent to Telegram instead (DECISIONS M-25).
- The PC needs this version too; with an older PC the phone hides muted notifications itself.

## v0.20.0 — A settings screen for the phone app

### Added
- **Settings has its own screen.** The phone app's PC list is now only the list; a gear button on
  the top bar opens Settings with four sections: notifications (push), display, updates and about.
  The gear shows an amber dot when push is on but Android blocks its notifications.
- **Light or dark, your choice.** Settings → 화면 picks "시스템 설정 따르기" (default), "밝게" or
  "어둡게"; the change applies at once, navigation bar included.
- **About section.** Links to this version's release notes, the Windows installer for another PC
  (latest release), the GitHub repository, a new GitHub issue with the app and Android versions
  filled in, and the open-source licenses. It also states what leaves the phone: the update check
  (GitHub) and end-to-end encrypted push (the distributor's relay) — no ads, analytics or usage data.
- The first-run screen links to the Windows installer for people who haven't set up the PC yet.

### Changed
- Push turning itself on after the first pairing, and the notification-permission prompt, no longer
  depend on the PC list being open; they run on every screen except pairing.

## v0.19.0 — Pull down to refresh

### Added
- **Pull down on the phone app's PC list to refresh.** It checks every PC again right away instead
  of waiting for the next 15-second poll, and checks GitHub for a new app version now instead of
  waiting for the once-a-day check. A short message says whether a new version exists, the app is
  up to date, or the check failed; a new version also shows the update banner. With "업데이트 확인"
  turned off, it only refreshes the PCs and doesn't contact GitHub.
- A version you chose to skip ("이 버전 건너뛰기") is offered again when you pull to refresh, since
  you asked to check. The notification for that version still doesn't repeat.

## v0.18.1 — The phone finds the PC again after a restart

### Fixed
- **A paired phone no longer loses the PC after the PC restarts.** The Cloudflare Quick Tunnel
  address changes on every Agent start (an update, a reboot), and the app only learned new paths
  after it had connected, so a phone that could reach the PC only through the tunnel showed
  "PC에 닿지 않습니다" until it was paired again. The PC now pushes its new paths to paired phones
  (signed per phone, so one PC can't rewrite another's), and a retrying phone picks them up on its
  next try. Needs push notifications on in the app; without them the screen now says to re-pair
  (DECISIONS M-23).
- **Pairing the same phone again no longer leaves a second entry on the PC.** The old entry kept
  receiving every alert, so the phone got each push twice. The PC now removes the earlier pairing
  when the phone registers for push, and cleans up duplicates left by earlier versions when it
  starts (M-24).
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
