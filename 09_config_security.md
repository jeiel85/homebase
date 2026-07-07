# 09. Configuration and Security

> **설계 보강 반영**: 이 문서는 `16_design_supplement.md §4` (설정 파일 구조)에 의해 보강되었다.

## 1. 설정 파일 위치 및 분리

설정은 3개 계층으로 분리한다.

| 파일 | 위치 | 내용 | Git |
|---|---|---|---|
| `appsettings.json` | `%ProgramData%/LocalOpsBot/config/` | 전체 설정, botToken은 `ENV:` 형식 | 포함 금지 |
| `appsettings.secrets.json` | `%ProgramData%/LocalOpsBot/config/` | 실제 token/chat_id (v2 DPAPI) | 포함 금지 |
| `appsettings.example.json` | 설치 패키지 내부 | 예시값, token dummy | 포함 |

Development:

```text
./appsettings.Development.json
```

## 2. Config loading 우선순위

```text
1. 환경변수 (LOCALOPSBOT__{SECTION}__{KEY})
2. %ProgramData%/LocalOpsBot/config/appsettings.json
3. %ProgramData%/LocalOpsBot/config/appsettings.secrets.json  (v2)
4. 실행 파일 경로의 appsettings.json (기본값)
```

## 3. Secret 처리 — ENV: prefix resolver

Telegram token은 환경변수로 제공한다.

```powershell
[Environment]::SetEnvironmentVariable("LOCALOPSBOT_TELEGRAM_TOKEN", "123:abc", "Machine")
```

Config 파일에는 다음 형식으로 작성:

```json
{
  "telegram": {
    "botToken": "ENV:LOCALOPSBOT_TELEGRAM_TOKEN"
  }
}
```

Agent는 `EnvpPrefixConfigurationProvider`를 통해 `ENV:` prefix를 감지하고 환경변수값으로 치환한다. (구현: `16_design_supplement.md §4.3`)

### DPAPI 암호화 (v3, 장기 목표)

```text
localops secret set telegram.botToken
→ %ProgramData%/LocalOpsBot/config/token.encrypted (DPAPI ProtectedData)
```

Agent는 `DPAPI:path/to/file.encrypted` 형식을 읽어 복호화한다.

## 3. 설정 schema

전체 샘플은 `schemas/appsettings.sample.json` 참고.

주요 섹션:

```text
telegram
agent
alerting
collectors
processWatches
serviceWatches
eventLog
notificationForwarding
developerMonitors
retention
logging
```

## 4. Config validation

Startup 시 다음을 검증한다.

### Fatal

- telegram.botToken missing
- telegram.allowedChatIds empty
- pollingTimeoutSeconds < 5
- database path invalid
- collector interval < 10 seconds

### Warning

- notification forwarding enabled but tray not connected
- no process watches configured
- event log warning level enabled without provider filter

## 5. Telegram 보안

### 5.1 Allowlist

무조건 적용한다.

```json
{
  "telegram": {
    "allowedChatIds": [123456789]
  }
}
```

### 5.2 Unauthorized behavior

기본값:

```json
{
  "telegram": {
    "respondToUnauthorized": false
  }
}
```

### 5.3 Audit log

허용되지 않은 요청은 command_log에 남긴다.

저장 필드:

- chat_id
- user_id
- raw_text truncated
- received_at
- status: Unauthorized

## 6. 원격 제어 정책

초기 버전에서는 read-only 명령만 제공한다.

위험 기능을 나중에 넣는다면 다음 조건을 모두 만족해야 한다.

- config에서 explicit enable
- command별 allowlist
- confirmation token
- audit log
- timeout
- rollback 가능성 검토

위험 기능 예시:

```json
{
  "dangerousCommands": {
    "enabled": false,
    "allowRestartService": false,
    "allowShutdown": false,
    "allowShell": false
  }
}
```

## 7. Notification privacy

### 7.1 기본 off

알림 포워딩은 기본적으로 꺼져 있어야 한다.

```json
{
  "notificationForwarding": {
    "enabled": false
  }
}
```

### 7.2 Block sensitive apps

기본 block apps:

```json
[
  "1Password",
  "Bitwarden",
  "Authy",
  "Microsoft Authenticator",
  "Windows Security"
]
```

### 7.3 Masking patterns

기본:

```json
[
  "(?<!\\d)\\d{6}(?!\\d)",
  "(?i)(password|passwd|pwd)\\s*[:=]\\s*\\S+",
  "(?i)bearer\\s+[a-z0-9._\\-]+",
  "(?i)(token|secret|api[_-]?key)\\s*[:=]\\s*\\S+"
]
```

## 8. 파일 권한

설치 스크립트는 `%ProgramData%/LocalOpsBot`에 다음 권한을 설정한다.

- Administrators: FullControl
- SYSTEM: FullControl
- 현재 사용자: Modify
- 일반 Users: Read 또는 없음. 사용 시 주의

## 9. 로그 마스킹

Telegram token, Authorization header, secret-like values는 로그에 남기지 않는다.

`SecretRedactor`를 공통으로 둔다.

```csharp
public interface ISecretRedactor
{
    string Redact(string text);
}
```

## 10. Config hot reload (v2)

`appsettings.json`의 다음 섹션은 서비스 재시작 없이 변경 감시한다:

- `alerting` (rate limit, dedup window)
- `processWatches` / `serviceWatches`
- `notificationForwarding` (allow/block list, masking)
- `eventLog` (filter, provider list)

.NET `IOptionsMonitor` + `FileConfigurationSource.ReloadOnChange = true` 사용.

`telegram.botToken`과 `telegram.allowedChatIds`는 재시작 필요 (hot reload 제외).

변경 감시는 5초 이내 반영을 목표로 한다.

## 11. Supply chain

NuGet 패키지는 최소화한다.

초기 추천:

- Microsoft 공식 패키지
- Serilog
- Microsoft.Data.Sqlite

Telegram은 단순 HTTP API로 직접 구현해도 충분하다. 의존성을 줄이고 싶으면 직접 구현을 추천한다.
