# 13. Testing Plan

> **설계 보강 반영**: Infrastructure mocking 전략 상세는 `16_design_supplement.md §3` 참조.

## 1. 테스트 전략

이 프로젝트는 PC 환경 의존성이 크므로 테스트를 세 층으로 나눈다.

1. Pure unit tests: Windows/Telegram 없이 실행
2. Integration tests: fake Telegram, temp SQLite 사용
3. Manual Windows tests: 실제 Service/Tray/Notification 확인

### 1.1 Infrastructure 모의 전략 개요

모든 Infrastructure 구현은 interface 뒤에 숨긴다. 테스트는 `Fake*` 구현체를 사용하며, interface에 test hook을 추가하지 않는다.

| 실제 구현 | 테스트 대체 |
|---|---|
| `TelegramClient` | `FakeTelegramClient` (Send/GetUpdates in-memory queue) |
| `WindowsSystemMetricsCollector` | `FakeSystemMetricsCollector` (고정값 또는 설정 가능) |
| `WindowsDiskCollector` | `FakeDiskCollector` (고정 드라이브 목록) |
| `WindowsEventLogWatcher` | `FakeEventLogWatcher` (메모리 아이템 목록) |
| `ProcessCollector` | `FakeProcessCollector` (프로세스 상태 설정 가능) |
| `WindowsServiceCollector` | `FakeWindowsServiceCollector` (서비스 상태 설정 가능) |

Fake 구현체는 `tests/LocalOpsBot.Tests/Fakes/`에 위치한다.

## 2. Unit tests

### CommandParserTests

Cases:

- `/ping` parses command name `ping`
- `/status@my_bot` removes bot suffix
- `/mute 1h` parses args
- empty message ignored
- non-command text ignored
- too long command rejected

### AllowedChatPolicyTests

Cases:

- allowed chat accepted
- unknown chat rejected
- empty allowlist fails config validation

### MessageFormatterTests

Cases:

- HTML special chars escaped
- null metrics show `unknown`
- disk bytes formatted as GB
- long event message truncated

### NotificationMaskerTests

Cases:

- 6-digit OTP masked
- bearer token masked
- password line masked
- normal message unchanged
- null/empty safe

### NotificationFilterTests

Cases:

- block list wins
- allow list only allows listed apps
- disabled forwarding drops all
- sensitive app dropped

### DeduplicationTests

Cases:

- same dedup key inside window blocked
- same key after window allowed
- different key allowed

### RateLimiterTests

Cases:

- max per minute enforced
- max per hour enforced
- critical override optional

## 3. Integration tests

### 3.1 TelegramPollingServiceTests

Use `FakeTelegramClient`.

Cases:

- updates processed in order
- offset saved after success
- unauthorized update not routed
- command handler exception returns error response
- Telegram getUpdates failure retries

### 3.2 수집 실패 내성 테스트

`FakeSystemMetricsCollector`의 `NextResult`를 `CollectorResult.Fail`로 설정하여 각 collector 실패 시 `/status` 응답에서 해당 항목만 `unknown`으로 표시되는지 검증. 다른 항목은 정상 출력.

| 시나리오 | 기대 |
|---|---|
| CPU 수집 실패 | CPU: `unknown`, 다른 항목 정상 |
| 모든 수집 실패 | 모든 항목 `unknown`, 전체 응답 실패는 아님 |
| 일부 드라이브 수집 실패 | 해당 드라이브만 `unknown` |

### AlertDispatcherTests

Use fake Telegram and temp SQLite.

Cases:

- alert saved after send
- failed send saved as failed
- dedup prevents duplicate send
- mute prevents non-critical alert

### 3.3 RepositoryTests

Use temp SQLite file.

Cases:

- migration creates tables (auto-create)
- runtime state set/get (key-value roundtrip)
- command log insert/update
- alert recent query (with filter)
- retention cleanup (오래된 데이터 삭제)
- concurrent read/write (SQLite WAL 모드 검증)

### 3.4 NotificationBridgePipeTests

Use in-memory `NamedPipeServerStream` / `NamedPipeClientStream` (local loopback).

Cases:

- Tray → Agent 메시지 전송 및 수신
- length-prefixed JSON 경계 조건
- 잘못된 JSON → Agent가 로그만 남기고 skip
- Agent 연결 끊김 → Tray 재연결

## 4. Manual tests

### MT-001 Service install

Steps:

1. run install-service.ps1 as admin
2. check service exists
3. check service starts
4. check log file created

Expected:

- service status Running
- Telegram boot message received

### MT-002 Telegram commands

Commands:

```text
/ping
/help
/status
/disk
/diagnostics
```

Expected:

- all respond within 3 seconds under normal network

### MT-003 Unauthorized chat

Steps:

1. Add bot to another chat or use another Telegram account
2. send `/ping`

Expected:

- no command execution
- unauthorized log entry

### MT-004 Disk warning

Use temporary config with high threshold.

```json
"warnUsedPercentAbove": 1
```

Expected:

- warning alert sent
- repeated alerts deduped

### MT-005 Process missing

Configure fake process watch.

Expected:

- missing alert sent

### MT-006 Toast permission

Steps:

1. start Tray
2. click "Request Permission" (calls `UserNotificationListener.Current.RequestAccessAsync()`)
3. Windows system dialog appears → Allow
4. generate test notification from another app
5. wait up to 3 seconds (polling interval)

Expected:

- Telegram receives forwarded notification
- notification contains correct app name, title, body
- stress test: Chrome, Edge, Slack, VS Code 각각 1회씩 포워딩 확인

### MT-007 Permission denied

Steps:

1. start Tray
2. click "Request Permission"
3. Windows system dialog appears → Deny

Expected:

- Tray shows "Forwarding disabled" status
- `RequestAccessAsync()` 다시 호출해도 재시도 가능
- Windows 설정 > 접근성 > 알림 수신기 에서 권한 변경 가능

### MT-008 Sensitive masking

Generate test notification:

```text
인증번호 123456
password: mysecret
```

Expected:

```text
인증번호 ******
password: ******
```

### MT-009 Agent-Tray IPC 복구

Steps:

1. Tray 실행, Agent 연결 확인
2. Agent 서비스 중지 (`Stop-Service LocalOpsBot.Agent`)
3. Tray 연결 끊김 표시 확인
4. Agent 서비스 시작 (`Start-Service LocalOpsBot.Agent`)

Expected:

- 중지 후 15초 이내 Tray 연결 끊김 감지
- 시작 후 15초 이내 Tray 자동 재연결
- 재연결 후 메시지 전송 정상 동작

## 5. Load/noise tests

### LT-001 Notification burst

Generate 100 notifications in 1 minute.

Expected:

- rate limiter applies
- service does not crash
- logs indicate dropped alerts

### LT-002 EventLog burst

Simulate repeated same event.

Expected:

- dedup prevents Telegram spam

## 6. Acceptance test checklist

Release candidate must pass:

- build succeeds
- unit tests pass
- service install/uninstall pass
- boot notification pass
- `/status` pass
- unauthorized chat blocked
- disk/process watcher pass
- Tray notification forwarding pass when enabled
- notification forwarding remains off by default
- secrets not logged
- token not committed
