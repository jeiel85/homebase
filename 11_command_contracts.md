# 11. Command Contracts

## 1. 공통 응답 규칙

- 모든 응답은 Telegram HTML escape 처리한다.
- code-like 값은 `<code>...</code>`로 감싼다.
- 긴 응답은 3500자 기준으로 분할한다.
- 실패한 항목은 전체 실패로 만들지 않고 항목별 `unknown` 표시한다.

## 2. /ping

### Request

```text
/ping
```

### Response

```text
pong
DESKTOP-01 | 2026-07-07 17:30:00 KST
```

## 3. /help

### Response

```text
<b>LocalOps Bot Commands</b>

/status - PC 상태 요약
/disk - 디스크 상태
/process - 감시 프로세스 상태
/services - 감시 서비스 상태
/events - 최근 Windows 이벤트
/alerts - 최근 알림 이력
/mute 1h - 자동 알림 음소거
/unmute - 음소거 해제
/diagnostics - Agent 진단
```

## 4. /status

### Response example

```text
<b>🖥 DESKTOP-01 Status</b>

Uptime: <code>3d 04h 12m</code>
CPU: <code>14%</code>
RAM: <code>11.2 / 31.8 GB (35%)</code>
Network: <code>Online</code>

<b>Disk</b>
C: <code>86.4 GB free / 476.2 GB</code>
D: <code>512.1 GB free / 931.5 GB</code>

<b>Watch</b>
PostgreSQL: ✅ Running
Ollama: ✅ Running
Local Web App: ❌ Down
```

## 5. /disk

### Response example

```text
<b>💾 Disk Status</b>

C:\ 389.8 / 476.2 GB used (81.8%)
Free: 86.4 GB
Status: OK

D:\ 419.4 / 931.5 GB used (45.0%)
Free: 512.1 GB
Status: OK
```

## 6. /process

### Response example

```text
<b>⚙️ Process Watch</b>

Ollama: ✅ Running (1)
PostgreSQL: ✅ Running (6)
Node Dev Server: ❌ Missing
Chrome: ✅ Running (12)
```

## 7. /services

### Response example

```text
<b>🧩 Windows Services</b>

postgresql-x64-16: ✅ Running
Docker Desktop Service: ✅ Running
Spooler: ✅ Running
```

## 8. /events

### Args

```text
/events
/events system
/events app
/events error 10
```

### Response example

```text
<b>🚨 Recent Windows Events</b>

[Application] Error 1000
Provider: Application Error
Time: 2026-07-07 17:25:00
Message: node.exe crashed...

[System] Error 7031
Provider: Service Control Manager
Time: 2026-07-07 15:10:00
Message: The service terminated unexpectedly...
```

## 9. /alerts

### Response example

```text
<b>🔔 Recent Alerts</b>

17:25 Critical Windows Event 1000
17:10 Warning C drive free space below 20GB
16:58 Recovery PostgreSQL recovered
```

## 10. /mute

### Request

```text
/mute 1h
/mute 30m
/mute 2h
```

### Response

```text
🔕 Automatic alerts muted until 2026-07-07 18:30 KST.
Commands still work.
```

### Parser

Allowed units:

- `m`: minutes
- `h`: hours

Limits:

- minimum: 5 minutes
- maximum: 24 hours

## 11. /unmute

Response:

```text
🔔 Automatic alerts enabled.
```

## 12. /diagnostics

Response:

```text
<b>🧪 LocalOps Diagnostics</b>

Agent started: 2026-07-07 09:00:00
Last Telegram poll: 2026-07-07 17:30:12
Last Telegram success: 2026-07-07 17:30:12
Last metric sample: 2026-07-07 17:30:00
Consecutive Telegram failures: 0
Consecutive DB failures: 0
Tray connected: Yes
Notification forwarding: On
Mute: Off
```

## 13. Unknown command

Response:

```text
Unknown command: /xxx
Use /help to see available commands.
```

## 14. Unauthorized update

Default behavior:

- Telegram 응답 없음
- command_log에 Unauthorized 기록
- warning log 남김

Optional debug behavior:

```text
Unauthorized chat.
```

## 15. Alert message contracts

### Boot

```text
🟢 <b>DESKTOP-01 boot detected</b>

Time: <code>2026-07-07 17:22:11 KST</code>
IP: <code>192.168.0.20</code>
Uptime: <code>00:01:03</code>
```

### Disk warning

```text
⚠️ <b>Disk space warning</b>

Drive: <code>C:\</code>
Free: <code>8.3 GB</code>
Used: <code>96.1%</code>
```

### Process missing

```text
⚠️ <b>Watched process missing</b>

Target: <code>Ollama</code>
Process: <code>ollama.exe</code>
```

### Toast notification

```text
🔔 <b>Chrome</b>

Build completed
Deployment succeeded
```
