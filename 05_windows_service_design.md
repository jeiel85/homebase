# 05. Windows Service Design

## 1. 역할

`LocalOpsBot.Agent`는 Windows Service로 실행되는 핵심 백그라운드 프로세스다.

담당 기능:

- Telegram polling
- boot notification
- scheduled metric collection
- disk/process/service watch
- Windows Event Log watch
- alert policy/rate limit/dedup
- SQLite persistence
- Tray IPC receiver

담당하지 않는 기능:

- 사용자의 Windows Toast 알림 접근 권한 요청
- 사용자 UI 표시
- tray icon
- 키보드/마우스 입력 감시

## 2. 실행 모델

.NET Worker Service 기반으로 구성한다.

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LocalOpsBot Agent";
});

builder.Services.AddHostedService<TelegramPollingService>();
builder.Services.AddHostedService<BootNotificationService>();
builder.Services.AddHostedService<WatchdogBackgroundService>();
builder.Services.AddHostedService<MetricSamplingService>();
builder.Services.AddHostedService<EventLogBackgroundService>();

await builder.Build().RunAsync();
```

## 3. Hosted Services

### 3.1 BootNotificationService

Type: `IHostedService`

StartAsync:

1. config validation
2. host info collection
3. dedup check
4. Telegram boot message send
5. event store

StopAsync:

- optional service stopped notification은 기본 비활성화

### 3.2 TelegramPollingService

Type: `BackgroundService`

Responsibilities:

- getUpdates loop
- command validation
- command routing
- offset persistence

### 3.3 WatchdogBackgroundService

Type: `BackgroundService`

Responsibilities:

- process watch
- Windows Service watch
- HTTP endpoint watch
- TCP port watch

Loop interval:

- default 60 seconds
- minimum 15 seconds, config validation으로 제한

### 3.4 MetricSamplingService

Type: `BackgroundService`

Responsibilities:

- CPU/RAM/disk/network sample
- SQLite metric 저장
- threshold alert 생성

Default interval:

- 60 seconds

### 3.5 EventLogBackgroundService

Type: `BackgroundService`

Responsibilities:

- EventLog watcher 등록
- Error/Critical event filter
- dedup 후 alert 생성

Fallback:

- watcher 실패 시 최근 N분 EventLog polling

### 3.6 NotificationBridgeBackgroundService

Type: `BackgroundService`

Responsibilities:

- Tray App에서 전달한 notification event 수신
- filter/masking 재검증
- Telegram alert dispatch

## 4. Configuration loading

Priority:

1. environment variables
2. `%ProgramData%/LocalOpsBot/config/appsettings.json`
3. appsettings.json packaged default

필수 설정 누락 시 fail-fast:

- Telegram bot token
- allowed chat IDs

선택 설정 누락 시 default 적용:

- collector intervals
- threshold values
- dedup windows

## 5. Service identity

초기 버전은 `LocalSystem`보다 제한적인 계정을 고려한다. 단, EventLog/Service query 권한 이슈가 생기면 설치 스크립트에서 관리자 권한으로 등록하고 기본 서비스 계정을 선택한다.

권장 순서:

1. LocalService로 가능한지 검토
2. 권한 부족 시 LocalSystem
3. 장기적으로 전용 local user 고려

## 6. Service recovery

설치 스크립트에서 다음 recovery 설정을 적용한다.

```powershell
sc.exe failure "LocalOpsBot.Agent" reset= 86400 actions= restart/60000/restart/60000/restart/300000
```

의미:

- 1차 실패: 60초 후 재시작
- 2차 실패: 60초 후 재시작
- 3차 실패: 5분 후 재시작
- failure count reset: 1일

## 7. Directory layout

```text
%ProgramFiles%/LocalOpsBot/
  Agent/
  Tray/

%ProgramData%/LocalOpsBot/
  config/appsettings.json
  data/localops.db
  logs/agent-.log
  logs/tray-.log
  queue/
```

## 8. Logging

Serilog file rolling policy:

```json
{
  "path": "%ProgramData%/LocalOpsBot/logs/agent-.log",
  "rollingInterval": "Day",
  "retainedFileCountLimit": 14,
  "fileSizeLimitBytes": 10485760,
  "rollOnFileSizeLimit": true
}
```

Log levels:

| Level | 사용 |
|---|---|
| Debug | collector 상세값, 개발 중만 |
| Information | service start/stop, command 처리 |
| Warning | threshold warning, recoverable error |
| Error | Telegram failure, DB failure, watcher failure |
| Fatal | config invalid, startup fail |

## 9. Health model

Agent 자체 health:

```csharp
public sealed record AgentHealth(
    DateTimeOffset StartedAt,
    DateTimeOffset LastTelegramPollAt,
    DateTimeOffset? LastTelegramSuccessAt,
    DateTimeOffset? LastMetricSampleAt,
    DateTimeOffset? LastAlertSentAt,
    int ConsecutiveTelegramFailures,
    int ConsecutiveDbFailures,
    bool TrayConnected);
```

`/diagnostics` 명령에서 사용한다.

## 10. Graceful shutdown

StopAsync 처리:

- polling cancellation
- watcher dispose
- pending alert queue flush, 최대 5초
- DB connection close
- log flush

## 11. Error handling rules

- HostedService 내부 예외는 반드시 잡아서 log 후 loop 지속
- config invalid는 startup fail
- Telegram 401은 반복 재시도하지 말고 명확한 오류 로그
- EventLog watcher 실패는 polling fallback
- SQLite write 실패는 file log만 남기고 기능 계속
