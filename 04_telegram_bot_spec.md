# 04. Telegram Bot Spec

## 1. 연동 방식

기본 방식은 `getUpdates` long polling이다.

선택 이유:

- 외부 포트 개방 불필요
- 별도 HTTPS endpoint 불필요
- 개인 PC 환경에 적합
- Telegram Bot API만 outbound HTTPS로 호출

## 2. Bot 생성 절차

1. Telegram에서 `@BotFather` 접속
2. `/newbot`
3. 봇 이름 입력
4. 봇 username 입력
5. Bot token 복사
6. 내 봇에게 아무 메시지나 전송
7. `getUpdates`로 chat_id 확인
8. `appsettings.json`에 token과 allowedChatIds 설정

## 3. 설정

```json
{
  "telegram": {
    "botToken": "ENV:LOCALOPSBOT_TELEGRAM_TOKEN",
    "allowedChatIds": [123456789],
    "pollingTimeoutSeconds": 30,
    "pollingErrorBackoffSeconds": 10,
    "parseMode": "HTML",
    "disableWebPagePreview": true
  }
}
```

## 4. 보안 정책

### 4.1 allowlist 필수

Telegram 봇 token을 아는 사람 또는 봇 username을 찾은 사람이 메시지를 보낼 수 있다. 따라서 실제 명령 처리는 `allowedChatIds` 안에 있는 chat_id만 허용한다.

Policy:

```text
if update.chat.id not in allowedChatIds:
    log unauthorized attempt
    do not execute command
```

선택 사항:

- unauthorized 사용자에게 응답하지 않기: 보안상 추천
- `Unauthorized`만 응답하기: 디버깅에는 편함

### 4.2 command scope

기본 명령은 모두 read-only여야 한다.

금지 기본 명령:

- `/shell`
- `/cmd`
- `/powershell`
- `/delete`
- `/download_sensitive`
- `/screenshot` 기본 활성화
- `/shutdown` 기본 활성화

위험 명령은 feature flag가 켜져 있고, 별도의 confirmation token이 있을 때만 허용한다.

## 5. 명령 목록

### 5.1 기본 명령

| 명령 | 설명 | 우선순위 |
|---|---|---|
| `/ping` | 봇 생존 확인 | P0 |
| `/help` | 명령 도움말 | P0 |
| `/status` | 전체 상태 요약 | P0 |
| `/uptime` | 부팅 후 경과 시간 | P0 |
| `/disk` | 디스크 상태 | P0 |
| `/process` | 주요 프로세스 상태 | P1 |
| `/services` | Windows Service 상태 | P1 |
| `/watch` | watch 대상 요약 | P1 |
| `/events` | 최근 이벤트 로그 | P1 |
| `/alerts` | 최근 알림 이력 | P1 |
| `/mute 1h` | 자동 알림 음소거 | P2 |
| `/unmute` | 음소거 해제 | P2 |
| `/policy` | 알림 정책 요약 | P2 |
| `/diagnostics` | Agent 자체 진단 | P2 |

### 5.2 개발자용 명령

| 명령 | 설명 |
|---|---|
| `/dev` | 개발 환경 상태 요약 |
| `/ports` | 감시 중인 포트 상태 |
| `/http` | 감시 중인 HTTP endpoint 상태 |
| `/llm` | Ollama/LM Studio 등 로컬 LLM 상태 |

## 6. 명령 파싱 규칙

Input:

```text
/status
/mute 1h
/events system 10
```

Parser output:

```csharp
public sealed record BotCommand(
    string Name,
    IReadOnlyList<string> Args,
    long ChatId,
    long UserId,
    string RawText,
    DateTimeOffset ReceivedAt);
```

Rules:

- command는 첫 token에서 `/` 제거 후 lower-case 처리
- bot username suffix 제거: `/status@my_bot` → `status`
- args는 whitespace 기준 split
- unknown command는 `/help` 안내
- 너무 긴 message는 무시

## 7. Polling loop

Pseudo:

```csharp
long? offset = await stateStore.GetLastOffsetAsync();

while (!ct.IsCancellationRequested)
{
    try
    {
        var updates = await telegram.GetUpdatesAsync(offset, timeoutSeconds: 30, ct);
        foreach (var update in updates)
        {
            await processor.ProcessAsync(update, ct);
            offset = update.UpdateId + 1;
            await stateStore.SaveLastOffsetAsync(offset.Value, ct);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        log.Error(ex, "Telegram polling failed");
        await Task.Delay(backoff, ct);
    }
}
```

## 8. 메시지 포맷

Telegram HTML parse mode를 기본으로 한다.

주의:

- `<`, `>`, `&`는 escape 필요
- 너무 긴 메시지는 여러 개로 분할
- MarkdownV2는 escape 규칙이 까다롭기 때문에 초기 버전에서는 HTML 권장

Example:

```text
<b>🖥 DESKTOP-01 Status</b>

Uptime: <code>3d 04h 12m</code>
CPU: <code>14%</code>
RAM: <code>11.2 / 31.8 GB (35%)</code>
Disk C: <code>86.4 GB free</code>
Network: <code>Online</code>
```

## 9. Rate limit

Telegram API 자체 제한도 고려해야 하지만, 개인 봇에서는 먼저 로컬 정책을 둔다.

Recommended local policy:

```json
{
  "alerting": {
    "maxMessagesPerMinute": 10,
    "maxMessagesPerHour": 120,
    "dedupWindowSeconds": 600
  }
}
```

## 10. 실패 처리

| 상황 | 처리 |
|---|---|
| 401 Unauthorized | token 오류. polling 중단, high severity log |
| 429 Too Many Requests | retry_after 준수 또는 exponential backoff |
| network timeout | retry/backoff |
| malformed update | skip and log |
| formatter error | fallback text 발송 |
