# 03. GOAL Roadmap

이 문서는 바이브 코딩 모델에게 하나씩 던질 수 있는 구현 단위입니다. 각 GOAL은 독립적으로 검증 가능한 완료 기준을 가집니다.

## GOAL-00: Repository Bootstrap

### 목적

C# 솔루션과 기본 프로젝트 구조를 만든다.

### 생성할 프로젝트

```text
LocalOpsBot.sln
src/LocalOpsBot.Core/LocalOpsBot.Core.csproj
src/LocalOpsBot.Infrastructure/LocalOpsBot.Infrastructure.csproj
src/LocalOpsBot.Data/LocalOpsBot.Data.csproj
src/LocalOpsBot.Agent/LocalOpsBot.Agent.csproj
src/LocalOpsBot.Tray/LocalOpsBot.Tray.csproj
tests/LocalOpsBot.Tests/LocalOpsBot.Tests.csproj
```

### NuGet 후보

- Microsoft.Extensions.Hosting
- Microsoft.Extensions.Hosting.WindowsServices
- Microsoft.Extensions.Configuration.Json
- Microsoft.Extensions.Http
- Microsoft.Data.Sqlite
- Serilog.Extensions.Hosting
- Serilog.Sinks.File
- System.Management
- System.ServiceProcess.ServiceController

### 완료 기준

- solution build 성공
- Agent console 실행 가능
- appsettings.example.json 포함

---

## GOAL-01: Telegram Basic Client

### 목적

Telegram Bot API에 메시지를 보낼 수 있는 클라이언트를 구현한다.

### 구현 대상

- `ITelegramClient`
- `TelegramClient`
- `TelegramOptions`
- `TelegramApiException`

### 메서드

```csharp
Task SendMessageAsync(long chatId, string text, CancellationToken ct);
Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long? offset, int timeoutSeconds, CancellationToken ct);
```

### 완료 기준

- 설정된 chat_id로 test message 발송
- HTTP 오류 발생 시 예외와 로그 확인
- token 미설정 시 앱 시작 실패

---

## GOAL-02: Command Polling and Allowlist

### 목적

Telegram 메시지를 polling하고 허용된 chat_id만 처리한다.

### 구현 대상

- `TelegramPollingService : BackgroundService`
- `ICommandRouter`
- `AllowedChatPolicy`
- `PingCommandHandler`

### 명령

```text
/ping
/help
```

### 완료 기준

- `/ping` → `pong`
- allowlist 외 chat_id → 응답하지 않거나 `Unauthorized` 로그만 기록
- update offset이 정상 증가
- polling 오류 시 서비스가 죽지 않고 backoff 후 재시도

---

## GOAL-03: Windows Service Boot Notification

### 목적

Agent를 Windows Service로 실행하고 부팅/서비스 시작 알림을 보낸다.

### 구현 대상

- `BootNotificationService : IHostedService`
- `IHostInfoProvider`
- `BootMessageFormatter`
- installer script

### 완료 기준

- `sc.exe` 또는 PowerShell로 서비스 등록 가능
- 서비스 시작 시 Telegram 부팅 알림 발송
- 중복 부팅 알림 방지: 10분 dedup

---

## GOAL-04: Basic System Status Collectors

### 목적

`/status`, `/uptime`, `/disk` 명령을 구현한다.

### 구현 대상

- `ISystemMetricsCollector`
- `IDiskCollector`
- `INetworkStatusChecker`
- `StatusCommandHandler`
- `DiskCommandHandler`
- `UptimeCommandHandler`

### 완료 기준

- `/status` 응답에 CPU/RAM/disk/network/uptime 포함
- `/disk` 응답에 드라이브별 total/free/used% 포함
- 수집 실패 시 해당 항목만 `unknown` 처리

---

## GOAL-05: SQLite Persistence

### 목적

명령, 알림, metric snapshot을 SQLite에 저장한다.

### 구현 대상

- `LocalOpsDbContext` 또는 lightweight repository
- `ICommandLogRepository`
- `IAlertLogRepository`
- `IMetricRepository`
- migration runner

### 완료 기준

- 앱 시작 시 DB와 테이블 자동 생성
- 명령 처리 결과 저장
- 알림 발송 결과 저장
- 최근 이벤트 조회 가능

---

## GOAL-06: Process and Service Watch

### 목적

설정된 프로세스/Windows Service 상태를 감시한다.

### 구현 대상

- `IProcessCollector`
- `IWindowsServiceCollector`
- `WatchPolicyEvaluator`
- `WatchdogBackgroundService`

### 명령

```text
/watch
/process
/services
```

### 완료 기준

- 설정된 프로세스 누락 시 알림
- 설정된 서비스 상태가 기대값과 다르면 알림
- 정상 복구 시 recovery 알림 선택 가능
- 동일 알림 dedup 적용

---

## GOAL-07: Windows Event Log Watch

### 목적

Application/System 로그에서 Error/Critical 이벤트를 감시한다.

### 구현 대상

- `IEventLogWatcher`
- `WindowsEventLogWatcher`
- `EventLogAlertFormatter`
- event filter options

### 명령

```text
/events
/events error
/events system
```

### 완료 기준

- 최근 Error/Critical 이벤트 조회 가능
- 새 이벤트 발생 시 알림 가능
- 같은 provider/eventId/message hash dedup

---

## GOAL-08: Tray App Bootstrap

### 목적

사용자 세션에서 실행되는 Tray App을 만든다.

### 구현 대상

- WPF Tray App
- tray icon
- settings window
- Agent connection indicator
- startup registration script

### 완료 기준

- 로그인 시 Tray App 자동 실행
- 트레이 메뉴에서 settings 열기 가능
- Agent 연결 상태 표시

---

## GOAL-09: Toast Notification Forwarding

### 목적

Windows Toast 알림을 읽어 Telegram으로 전달한다.

### 구현 대상

- `IUserNotificationReader`
- `WindowsUserNotificationReader`
- `NotificationFilter`
- `NotificationMasker`
- `NotificationBridgeClient`
- Agent-side `NotificationBridgeServer`

### 완료 기준

- 최초 실행 시 알림 접근 권한 요청
- 권한 허용 시 Toast 알림 수집
- allow/block list 적용
- 민감정보 마스킹
- Agent를 통해 Telegram 발송

---

## GOAL-10: Notification Policy and Quiet Hours

### 목적

알림 폭주 방지, 조용한 시간, rate limit 정책을 구현한다.

### 구현 대상

- `IAlertPolicy`
- `DeduplicationService`
- `RateLimiter`
- `QuietHoursService`

### 명령

```text
/mute 1h
/unmute
/policy
```

### 완료 기준

- 동일 알림 반복 제한
- 시간당 알림 수 제한
- `/mute` 동안 자동 알림 발송 중지
- 긴급 알림 override 옵션 제공

---

## GOAL-11: Developer Environment Monitors

### 목적

개발자 PC에 유용한 포트/로컬 서버/로컬 LLM 감시 기능을 추가한다.

### 구현 대상

- `HttpEndpointMonitor`
- `TcpPortMonitor`
- `DevProcessPresetProvider`

### 예시 설정

```json
{
  "name": "Local Web App",
  "url": "http://localhost:3000",
  "expectedStatusCodes": [200, 302]
}
```

### 완료 기준

- localhost endpoint 응답 감시
- port open/closed 감시
- Ollama, LM Studio, PostgreSQL preset 제공

---

## GOAL-12: Installer and Operations Hardening

### 목적

실사용 가능한 설치/삭제/로그/복구 스크립트를 제공한다.

### 구현 대상

- `install-service.ps1`
- `uninstall-service.ps1`
- `register-startup-tray.ps1`
- log directory creation
- service recovery config

### 완료 기준

- 관리자 PowerShell에서 설치 가능
- 재부팅 후 Agent 자동 시작
- 로그인 후 Tray 자동 시작
- uninstall 시 서비스 제거와 설정 보존 옵션 제공

---

## GOAL-13: Test Suite

### 목적

핵심 로직을 테스트 가능하게 만든다.

### 테스트 대상

- command parsing
- allowlist
- formatter
- notification masking
- deduplication
- rate limiter
- config validation
- Telegram client mock

### 완료 기준

- 단위 테스트 30개 이상
- command handler는 Telegram 없이 테스트 가능
- collector failure 테스트 포함

---

## GOAL-14: First Release Package

### 목적

개인 PC에 설치 가능한 첫 릴리스를 만든다.

### 산출물

```text
release/LocalOpsBot.Agent.zip
release/LocalOpsBot.Tray.zip
release/install.ps1
release/appsettings.example.json
release/README_FIRST_RUN.md
```

### 완료 기준

- fresh Windows 환경에서 설치 문서만 보고 설치 가능
- BotFather token/chat_id 설정 절차 문서화
- 장애 대응 문서 포함
