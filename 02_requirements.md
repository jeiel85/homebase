# 02. Requirements

## 1. 기능 요구사항

### FR-001 Telegram 연결

시스템은 Telegram Bot API를 사용하여 메시지를 발송할 수 있어야 한다.

Acceptance Criteria:

- `/ping` 명령에 `pong` 응답
- 허용되지 않은 chat_id에서 온 명령은 무시 또는 거부
- Telegram API 오류 발생 시 로그 기록

### FR-002 부팅 알림

시스템은 Windows Service 시작 시 부팅 알림을 보낸다.

Acceptance Criteria:

- 서비스 시작 후 60초 이내 알림 발송
- 컴퓨터 이름, 시간, 업타임, 주요 IP 포함
- 10분 이내 중복 부팅 알림 방지

### FR-003 상태 조회

사용자는 `/status` 명령으로 PC 상태를 조회할 수 있다.

Response Fields:

- host name
- OS version
- uptime
- CPU usage
- memory usage
- disk summary
- network status
- watched process/service summary

### FR-004 디스크 조회

사용자는 `/disk` 명령으로 드라이브별 용량을 조회할 수 있다.

### FR-005 프로세스 감시

시스템은 설정된 프로세스가 실행 중인지 감시한다.

Example:

```json
{
  "name": "Ollama",
  "processNames": ["ollama.exe"],
  "required": false,
  "alertWhenMissing": true
}
```

### FR-006 Windows Service 감시

시스템은 설정된 Windows Service 상태를 감시한다.

Example:

```json
{
  "name": "PostgreSQL",
  "serviceName": "postgresql-x64-16",
  "expectedStatus": "Running"
}
```

### FR-007 이벤트 로그 감시

시스템은 Application/System 로그의 Error/Critical 이벤트를 감시한다.

Filter:

- log name: Application, System
- level: Critical, Error
- optional provider include/exclude
- same event dedup window: default 10 minutes

### FR-008 Windows Toast 알림 포워딩

Tray App은 Windows Notification Listener 권한을 받은 뒤 Toast 알림을 Agent로 전달한다.

Acceptance Criteria:

- 사용자가 권한을 거부하면 기능 비활성화
- 앱별 allow/block list 적용
- 민감 문자열 마스킹
- 중복 알림 방지

### FR-009 조용한 시간

사용자는 알림을 일정 시간 음소거할 수 있다.

Commands:

- `/mute 1h`
- `/mute 30m`
- `/unmute`

### FR-010 이벤트 이력 조회

사용자는 최근 알림/이벤트 이력을 조회할 수 있다.

Commands:

- `/events`
- `/events error`
- `/alerts`

## 2. 비기능 요구사항

### NFR-001 보안

- Telegram token을 Git에 커밋하지 않는다.
- chat_id allowlist를 필수로 한다.
- 기본적으로 명령은 조회 전용이다.
- 위험 명령은 feature flag 없이는 동작하지 않는다.

### NFR-002 안정성

- Agent는 Windows Service로 자동 시작한다.
- 서비스 실패 시 자동 재시작한다.
- Telegram 장애 시 exponential backoff를 적용한다.
- SQLite 오류가 발생해도 서비스 전체가 죽지 않는다.

### NFR-003 성능

- idle CPU 사용률은 1% 이하를 목표로 한다.
- 기본 metric 수집 주기는 30초 이상으로 한다.
- EventLog/Toast 이벤트는 deduplication한다.

### NFR-004 개인정보

- Toast 알림 본문은 필터링과 마스킹을 거친다.
- OTP, 인증번호, password-like 문구는 기본 마스킹한다.
- 특정 앱은 block list에 기본 포함한다.

Default blocked apps:

- 1Password
- Bitwarden
- Authy
- Microsoft Authenticator
- Windows Security, 조건부: 보안 경고 제목만 허용하고 상세 본문은 마스킹

### NFR-005 운영성

- 로그 파일 위치는 `%ProgramData%/LocalOpsBot/logs`.
- 설정 파일 위치는 `%ProgramData%/LocalOpsBot/config/appsettings.json`.
- DB 위치는 `%ProgramData%/LocalOpsBot/data/localops.db`.
- `/diagnostics` 명령으로 현재 설정 요약과 최근 오류 확인 가능.

## 3. 플랫폼 요구사항

| 항목 | 기준 |
|---|---|
| OS | Windows 10 1607 이상 권장, Windows 11 권장 |
| Runtime | .NET 9+ |
| Telegram | BotFather로 생성한 bot token |
| 권한 | Service 설치 시 관리자 권한 필요 |
| Toast Forwarding | 사용자 세션 앱과 알림 접근 권한 필요 |

## 4. 우선순위

| 우선순위 | 기능 |
|---|---|
| P0 | Telegram 연결, 부팅 알림, `/status`, allowlist |
| P1 | disk/process/service/event log watch, SQLite |
| P2 | Tray App, Toast forwarding, filtering/masking |
| P3 | installer, auto update, advanced developer monitors |
| P4 | screenshot, remote control, multi-machine dashboard |
