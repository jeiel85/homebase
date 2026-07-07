# 01. Architecture

## 1. 전체 구조

```text
┌─────────────────────────────────────────────┐
│ Telegram Mobile/Desktop                     │
└───────────────────┬─────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────┐
│ Telegram Bot API                            │
│ - getUpdates                                │
│ - sendMessage                               │
│ - sendPhoto/document: optional              │
└───────────────────┬─────────────────────────┘
                    │ outbound HTTPS only
                    ▼
┌─────────────────────────────────────────────┐
│ Local Windows PC                            │
│                                             │
│ ┌─────────────────────────────────────────┐ │
│ │ LocalOpsBot.Agent                       │ │
│ │ Windows Service                         │ │
│ │ - boot notification                     │ │
│ │ - status collectors                     │ │
│ │ - event log watcher                     │ │
│ │ - process/service watcher               │ │
│ │ - Telegram polling                      │ │
│ └───────────────────┬─────────────────────┘ │
│                     │ named pipe / loopback │
│ ┌───────────────────▼─────────────────────┐ │
│ │ LocalOpsBot.Tray                        │ │
│ │ User Session App                        │ │
│ │ - notification permission               │ │
│ │ - Toast notification listener           │ │
│ │ - local settings UI                     │ │
│ └───────────────────┬─────────────────────┘ │
│                     │                       │
│ ┌───────────────────▼─────────────────────┐ │
│ │ LocalOpsBot.Data                        │ │
│ │ SQLite                                  │ │
│ │ - event history                         │ │
│ │ - command history                       │ │
│ │ - notification cache                    │ │
│ └─────────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
```

## 2. 프로젝트 구성

```text
LocalOpsBot/
├─ src/
│  ├─ LocalOpsBot.Agent/
│  ├─ LocalOpsBot.Tray/
│  ├─ LocalOpsBot.Core/
│  ├─ LocalOpsBot.Infrastructure/
│  ├─ LocalOpsBot.Data/
│  └─ LocalOpsBot.Tests/
├─ installer/
│  ├─ install-service.ps1
│  ├─ uninstall-service.ps1
│  └─ register-startup-tray.ps1
├─ docs/
├─ config/
│  └─ appsettings.example.json
└─ README.md
```

## 3. 컴포넌트 책임

### 3.1 LocalOpsBot.Core

공통 도메인 모델과 인터페이스를 가진다.

- `MetricSnapshot`
- `DiskSnapshot`
- `ProcessStatus`
- `ServiceStatus`
- `NotificationEvent`
- `BotCommand`
- `AlertEvent`
- collector interfaces
- command handler interfaces

Core는 Windows API, Telegram API, SQLite 같은 구체 구현에 의존하지 않는다.

### 3.2 LocalOpsBot.Infrastructure

외부 시스템 접근 구현을 가진다.

- Telegram HTTP client
- Windows metric collectors
- WMI watchers
- EventLog watchers
- Notification bridge client
- secret provider
- clock abstraction

### 3.3 LocalOpsBot.Data

SQLite 저장소 구현을 가진다.

- command log repository
- alert log repository
- notification dedup repository
- metric sample repository
- schema migration

### 3.4 LocalOpsBot.Agent

Windows Service 실행 엔트리다.

- host builder
- background services
- scheduled collectors
- Telegram polling loop
- alert dispatcher
- health check loop

### 3.5 LocalOpsBot.Tray

사용자 세션 앱이다.

- tray icon
- notification permission request
- notification listener
- app allow/block filter
- test notification
- local settings shortcut
- Agent 연결 상태 표시

## 4. 런타임 프로세스

### 4.1 서비스 시작

```text
1. Windows starts.
2. Service Control Manager starts LocalOpsBot.Agent.
3. Agent loads config.
4. Agent validates Telegram token and allowed chat IDs.
5. Agent writes service_started event to SQLite.
6. Agent sends boot notification.
7. Agent starts polling loop and scheduled monitors.
```

### 4.2 Telegram 명령 처리

```text
1. Agent calls getUpdates(offset, timeout=30).
2. Telegram returns updates.
3. Agent validates chat_id.
4. Agent parses message text.
5. CommandRouter finds handler.
6. Handler collects required data.
7. ResponseFormatter creates Telegram-safe text.
8. TelegramClient sends response.
9. Command result is stored in SQLite.
10. offset is advanced only after successful processing.
```

### 4.3 자동 알림 처리

```text
1. Monitor detects condition.
2. AlertPolicy evaluates severity and threshold.
3. Deduplication checks recent equivalent alert.
4. RateLimiter checks per-kind quota.
5. AlertFormatter formats text.
6. TelegramClient sends message.
7. Alert event is stored.
```

### 4.4 Windows Toast 알림 포워딩

```text
1. User starts Tray app.
2. Tray asks notification listener permission.
3. User grants permission.
4. Tray listens/polls user notifications.
5. Tray extracts app name/title/body.
6. Tray applies allow/block filters.
7. Tray masks sensitive patterns.
8. Tray passes event to Agent through local IPC.
9. Agent dispatches Telegram notification.
```

## 5. IPC 설계

Named Pipe를 단일 IPC 방식으로 채택한다. File queue fallback은 사용하지 않는다 (결정 근거는 `16_design_supplement.md §2` 참조).

### Pipe 사양

| 항목 | 값 |
|---|---|
| Pipe name | `\\.\pipe\LocalOpsBot.NotificationPipe` |
| Server (생성) | Agent (Windows Service) |
| Client (연결) | Tray (User Session App) |
| 방향 | 단방향 (Tray → Agent) |
| 보안 | `PipeSecurity`로 SYSTEM+Admin+현재 사용자만 접근 |
| 전송 | `PipeTransmissionMode.Byte` (스트림) |

### 프로토콜

length-prefixed JSON:

```text
[4바이트 little-endian 길이][UTF-8 JSON 본문]
```

Message:

```json
{
  "schemaVersion": 1,
  "type": "toast_notification",
  "eventId": "1a7f3d5e-3a7b-4c5a-9b1f-111111111111",
  "sourceApp": "Chrome",
  "title": "Build completed",
  "body": "Deployment succeeded",
  "createdAt": "2026-07-07T08:30:00.000Z",
  "sensitivity": "Normal"
}
```

### 연결 수명

Tray 시작 시 Agent 파이프 연결 시도 (3초 timeout, 5회 재시도). 연결 유지 후 메시지 전송 시마다 Write. 연결 끊김 감지 시 15초 후 재연결.

## 6. 실패 격리

| 실패 컴포넌트 | 영향 | 복구 |
|---|---|---|
| Telegram API 장애 | 알림 발송 실패 | retry/backoff, local queue |
| Agent 종료 | 전체 모니터링 중단 | Windows Service Recovery |
| Tray 종료 | Toast 포워딩만 중단 | Startup 등록, Agent는 계속 동작 |
| SQLite 잠김 | 로그 저장 실패 | short retry, fallback file log |
| WMI watcher 실패 | 프로세스 이벤트 감시 중단 | scheduled polling fallback |

## 7. 보안 경계

- Telegram token은 평문 repo에 포함 금지
- chat_id allowlist 적용
- 기본값은 read-only monitoring
- 원격 shell command는 구현하지 않음
- 원격 reboot/shutdown은 별도 feature flag 필요
- Tray notification permission은 사용자 명시 승인 필요
- 민감 알림 마스킹 기본 활성화
