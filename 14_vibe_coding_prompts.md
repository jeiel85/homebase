# 14. Vibe Coding Prompts

이 파일은 구현 모델에게 GOAL 단위로 전달할 수 있는 프롬프트입니다. 한 번에 전체를 맡기지 말고 순서대로 진행하세요.

## 공통 지시문

```text
너는 C#/.NET Windows 애플리케이션을 구현하는 시니어 개발자다.
아래 설계서의 범위를 벗어나지 말고, 이번 GOAL의 완료 기준만 구현하라.
보안상 Telegram token을 코드에 하드코딩하지 마라.
모든 외부 호출은 인터페이스 뒤에 숨기고 테스트 가능하게 작성하라.
구현 후 변경 파일 목록, 실행 방법, 테스트 방법을 보고하라.
```

## GOAL-00 Prompt

```text
LocalOpsBot 저장소의 기본 C# 솔루션 구조를 만들어줘.

생성할 프로젝트:
- LocalOpsBot.Core
- LocalOpsBot.Infrastructure
- LocalOpsBot.Data
- LocalOpsBot.Agent
- LocalOpsBot.Tray
- LocalOpsBot.Tests

요구사항:
- .NET 8 기준
- nullable enable
- implicit usings enable
- Directory.Build.props 사용
- Agent는 Worker Service 프로젝트로 구성
- Core에는 외부 구현 의존성 금지
- solution build 성공해야 함

완료 후 dotnet build 결과와 생성 구조를 설명해줘.
```

## GOAL-01 Prompt

```text
Telegram Basic Client를 구현해줘.

구현 대상:
- ITelegramClient
- TelegramClient
- TelegramOptions
- TelegramUpdate/Message/Chat/User model
- SendMessageAsync
- GetUpdatesAsync

요구사항:
- HttpClientFactory 사용
- Telegram token은 options에서 주입
- sendMessage는 POST 사용
- getUpdates는 offset/timeout 지원
- Telegram API 오류를 TelegramApiException으로 래핑
- HTML parse mode 지원
- 단위 테스트는 fake HttpMessageHandler로 작성

완료 기준:
- 테스트에서 sendMessage request URL/body 검증
- getUpdates JSON parse 검증
```

## GOAL-02 Prompt

```text
Telegram polling과 command router를 구현해줘.

구현 대상:
- TelegramPollingService : BackgroundService
- BotCommandParser
- ICommandHandler
- ICommandRouter
- AllowedChatPolicy
- PingCommandHandler
- HelpCommandHandler
- RuntimeStateRepository는 일단 in-memory 구현 가능

요구사항:
- allowedChatIds 외 요청은 실행 금지
- update offset 저장
- polling 오류 시 backoff 후 계속 진행
- /ping, /help 명령 지원
- command parser 테스트 작성

완료 기준:
- /ping 처리 테스트 통과
- unauthorized chat이 handler에 도달하지 않는 테스트 통과
```

## GOAL-03 Prompt

```text
Windows Service 부팅 알림 기능을 구현해줘.

구현 대상:
- Agent Program.cs WindowsService 설정
- BootNotificationService
- IHostInfoProvider
- BootMessageFormatter
- install-service.ps1
- uninstall-service.ps1

요구사항:
- Microsoft.Extensions.Hosting.WindowsServices 사용
- 서비스명 LocalOpsBot.Agent
- 서비스 시작 시 Telegram으로 boot notification 발송
- 10분 내 중복 부팅 알림 dedup
- 컴퓨터 이름, 시간, IP, uptime 포함

완료 기준:
- console 실행에서도 boot notification 테스트 가능
- PowerShell 설치 스크립트 포함
```

## GOAL-04 Prompt

```text
/status, /uptime, /disk 명령을 구현해줘.

구현 대상:
- ISystemMetricsCollector
- IDiskCollector
- INetworkStatusChecker
- StatusCommandHandler
- UptimeCommandHandler
- DiskCommandHandler
- StatusMessageFormatter

요구사항:
- collector 실패가 전체 명령 실패로 이어지지 않게 할 것
- disk는 DriveInfo 기반으로 구현
- uptime은 LastBootUpTime 또는 Environment.TickCount64 기반
- 응답은 Telegram HTML escape 처리

완료 기준:
- 각 command handler 단위 테스트
- 사람이 읽기 좋은 GB/percent 포맷
```

## GOAL-05 Prompt

```text
SQLite persistence를 구현해줘.

구현 대상:
- DatabaseMigrator
- RuntimeStateRepository
- CommandLogRepository
- AlertLogRepository
- MetricRepository
- sqlite_schema.sql 반영

요구사항:
- Microsoft.Data.Sqlite 사용
- 앱 시작 시 migration 자동 실행
- DB 파일 경로는 설정에서 받음
- command 시작/완료 로그 저장
- alert 저장과 recent query 구현
- 테스트는 temp db 사용

완료 기준:
- migration test 통과
- runtime state get/set test 통과
- command log insert/update test 통과
```

## GOAL-06 Prompt

```text
Process와 Windows Service watch를 구현해줘.

구현 대상:
- ProcessCollector
- WindowsServiceCollector
- WatchdogBackgroundService
- WatchPolicyEvaluator
- ProcessCommandHandler
- ServicesCommandHandler
- WatchCommandHandler

요구사항:
- processNames는 .exe 유무와 대소문자 차이를 흡수
- ServiceController 사용
- missing/down 상태에서 alert 생성
- recovery alert 옵션 지원
- dedup 적용

완료 기준:
- fake process/service collector로 evaluator 테스트
- /watch 응답 포맷 테스트
```

## GOAL-07 Prompt

```text
Windows Event Log 감시를 구현해줘.

구현 대상:
- WindowsEventLogWatcher
- EventLogBackgroundService
- EventLogCommandHandler
- EventLogAlertFormatter

요구사항:
- Application/System 로그의 Error/Critical 기본 감시
- provider include/exclude config 지원
- event message는 500자 truncate
- 같은 eventId/provider/message hash dedup
- watcher 실패 시 polling fallback 구조를 둬라

완료 기준:
- /events 명령이 최근 이벤트를 반환
- formatter 테스트 통과
```

## GOAL-08 Prompt

```text
WPF Tray App 기본 구조를 구현해줘.

구현 대상:
- LocalOpsBot.Tray WPF project
- tray icon
- context menu
- settings window skeleton
- Agent connection status 표시
- startup registration script

요구사항:
- 알림 포워딩 기능은 아직 구현하지 말고 UI skeleton만 만든다
- 종료 메뉴 제공
- logs folder 열기 메뉴 제공
- appsettings 위치 열기 메뉴 제공

완료 기준:
- 로그인 세션에서 tray icon 표시
- 메뉴 동작 확인
```

## GOAL-09 Prompt

```text
Windows Toast Notification forwarding을 구현해줘.

구현 대상:
- WindowsUserNotificationReader
- NotificationFilter
- NotificationMasker
- NotificationBridgeClient
- NotificationBridgeServer
- ToastNotificationMessageFormatter

요구사항:
- UserNotificationListener 권한 요청은 UI thread에서 수행
- forwarding은 기본 off
- allowApps/blockApps 적용
- OTP/token/password masking 적용
- Agent와 Tray는 Named Pipe로 통신
- Named Pipe 실패 시 file queue fallback 가능하게 구조화

완료 기준:
- test notification이 Telegram으로 전송됨
- 민감정보 마스킹 테스트 통과
- blockApps 테스트 통과
```

## GOAL-10 Prompt

```text
알림 정책을 강화해줘.

구현 대상:
- AlertPolicy
- DeduplicationService
- RateLimiter
- QuietHoursService
- MuteCommandHandler
- UnmuteCommandHandler
- PolicyCommandHandler

요구사항:
- dedup window config 적용
- max messages per minute/hour 적용
- /mute 1h, /mute 30m 지원
- mute 상태는 SQLite runtime_state에 저장
- critical alert override 옵션은 config로 제어

완료 기준:
- dedup/rate/mute unit test 통과
```

## GOAL-11 Prompt

```text
개발자 환경 모니터를 추가해줘.

구현 대상:
- HttpEndpointMonitor
- TcpPortMonitor
- DevStatusCommandHandler
- PortsCommandHandler
- Local LLM preset 상태 표시

요구사항:
- localhost HTTP endpoint health check
- TCP port open check
- Ollama 기본 포트 11434 preset
- PostgreSQL 5432 preset
- 응답 시간 표시

완료 기준:
- /dev, /ports 명령 응답
- endpoint down alert 테스트
```

## GOAL-12 Prompt

```text
설치/운영 안정화 작업을 해줘.

구현 대상:
- install.ps1
- uninstall.ps1
- register-startup-tray.ps1
- README_FIRST_RUN.md
- release packaging script

요구사항:
- 관리자 권한 확인
- Program Files/ProgramData 경로 생성
- service recovery 설정
- config sample 복사
- keep-data/purge uninstall 옵션
- 설치 후 /ping 테스트 안내

완료 기준:
- fresh Windows에서 문서대로 설치 가능
```

## GOAL-13 Prompt

```text
테스트 커버리지를 보강해줘.

대상:
- command parser
- allowlist
- Telegram formatter
- notification masker/filter
- dedup/rate limiter
- config validation
- repository

요구사항:
- 테스트 이름은 Given_When_Then 스타일
- 외부 Telegram 호출 금지
- Windows 환경 의존 테스트는 분리

완료 기준:
- dotnet test 통과
- 핵심 로직 테스트 30개 이상
```
