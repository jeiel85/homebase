# 06. Tray Notification Forwarder Design

> **설계 보강 반영**: 이 문서는 `16_design_supplement.md §1` (Toast API 검증), `§2` (IPC 결정)에 의해 보강되었다.

## 1. 역할

`LocalOpsBot.Tray`는 사용자 로그인 세션에서 실행되는 보조 앱이다.

담당 기능:

- tray icon 표시
- 알림 접근 권한 요청
- Windows Toast notification 읽기
- 앱별 allow/block filter
- 민감정보 masking
- Agent로 notification event 전달
- 사용자가 포워딩 on/off 제어

## 2. 왜 Service와 분리하는가

Windows Service는 사용자 데스크톱 세션과 분리된다. Windows Toast 알림 접근은 사용자의 명시적 권한과 사용자 세션 컨텍스트가 필요하다. 따라서 Service에서 직접 처리하지 않고 Tray App에서 처리한다.

## 3. 기술 선택

### 권장: WPF Tray App

이유:

- C#/.NET과 잘 맞음
- 구현 난이도 낮음
- Tray icon 구현 자료가 많음
- WinUI 3보다 바이브 코딩 안정성이 높음

WinUI 3는 디자인은 좋지만, 단순 tray/notification bridge 용도로는 초기 복잡도가 더 높다.

## 4. UI 구성

### Tray menu

```text
LocalOps Bot
├─ Status: Agent connected
├─ Notification Forwarding: On
├─ Request Notification Permission
├─ Send Test Notification
├─ Open Settings
├─ Open Logs Folder
└─ Exit
```

### Settings window

Sections:

- Telegram 연결 상태: read-only
- Notification forwarding on/off
- App allow list
- App block list
- Masking patterns
- Quiet hours
- Agent connection status

## 5. Notification permission flow

```text
1. User starts Tray App.
2. Tray checks notification listener access status.
3. If not allowed, show explanation window.
4. User clicks Request Permission.
5. App calls RequestAccessAsync on UI thread.
6. If allowed, start notification reader.
7. If denied, disable forwarding and show status.
```

## 6. Notification read strategy (확정)

### 사용 API

`Windows.UI.Notifications.Management.UserNotificationListener` WinRT API를 사용한다.

```csharp
// TFM: net8.0-windows10.0.17763.0 이상 (CsWinRT 내장)
using Windows.UI.Notifications.Management;

UserNotificationListener listener = UserNotificationListener.Current;
UserNotificationListenerAccessStatus status = await listener.RequestAccessAsync();
// status == Allowed 일 때만 GetNotificationsAsync 호출 가능

IReadOnlyList<UserNotification> notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
```

### Strategy: Periodic read (유일한 옵션)

`UserNotificationListener`는 event-driven API를 제공하지 않는다. 주기 폴링만 가능하다.

```text
간격: 3초
방식: 모든 알림 목록 조회 → 이전에 본 Id와 비교 → 신규 알림만 선별
과거 ID 추적: 메모리 HashSet<uint>, 최대 1000개, 5분마다 정리
```

### 알림 내용 추출

```text
UserNotification notification = ...;
UserNotificationContent content = notification.Notification.Visual.GetBinding();
IReadOnlyList<AdaptiveNotificationText> texts = content.GetTextElements();

title = texts.Count > 0 ? texts[0].Text : null;
body  = texts.Count > 1 ? texts[1].Text : null;
sourceApp = notification.AppInfo.DisplayInfo.DisplayName;
```

Toast 구조가 앱마다 다르므로 모든 인덱스 접근은 null-safe 처리해야 한다.

## 7. Notification event model

```csharp
public sealed record ToastNotificationEvent(
    string EventId,
    string SourceApp,
    string? Title,
    string? Body,
    DateTimeOffset CreatedAt,
    string RawNotificationId,
    NotificationSensitivity Sensitivity);
```

## 8. Filtering

### 8.1 block list 우선

```text
if sourceApp in blockApps:
    drop
```

### 8.2 allow list 모드

설정에 `allowApps`가 비어있으면 block list만 적용한다.

`allowApps`가 하나 이상 있으면 allow list에 포함된 앱만 포워딩한다.

```text
if allowApps not empty and sourceApp not in allowApps:
    drop
```

## 9. Masking

기본 마스킹 패턴:

| 대상 | 예시 | 마스킹 |
|---|---|---|
| 6자리 인증번호 | `123456` | `******` |
| 긴 숫자 토큰 | `123456789012` | `************` |
| password 키워드 라인 | `password: abc` | `password: ******` |
| bearer token | `Bearer ey...` | `Bearer ******` |
| 이메일 일부 | 선택 | `u***@domain.com` |

Masker interface:

```csharp
public interface ITextMasker
{
    string Mask(string input);
}
```

## 10. Agent bridge

### 10.1 Named Pipe protocol

Pipe name: `\\.\pipe\LocalOpsBot.NotificationPipe`
전송: `PipeTransmissionMode.Byte` (length-prefixed JSON)

#### Wire format

```text
[4바이트 little-endian 메시지 길이][UTF-8 JSON 본문]
```

#### Message schema

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

`sensitivity` 값: `Normal` / `Sensitive` / `Blocked`.

#### Connection lifecycle

Tray 시작 시 Agent 파이프 연결 시도 (3초 timeout, 5회 재시도, 1초 간격). 연결 유지 후 메시지 전송 시마다 Write. 연결 끊김 감지 시 15초 후 재연결.

### 10.2 File queue fallback (제거)

초기 설계에 있던 file queue fallback은 Named Pipe로 단일화하면서 제거한다. 이유: 동시성 위험, orphan 파일 누적, 디스크 I/O 지연. 자세한 결정 근거는 `16_design_supplement.md §2` 참조.

## 11. 중복 제거

Dedup key:

```text
sha256(sourceApp + title + body + createdAtRoundedToMinute)
```

Tray와 Agent 양쪽에서 중복 제거한다.

- Tray: UI/API 반복 수집 방지
- Agent: Telegram 중복 발송 방지

## 12. Failure handling

| 상황 | 처리 |
|---|---|---|
| 권한 없음 | forwarding disabled 상태 표시 |
| Agent pipe 연결 실패 | 15초 후 재연결, 사용자에게 연결 끊김 표시 |
| Telegram 실패 | Agent가 처리하므로 Tray는 신경 쓰지 않음 |
| 설정 파일 읽기 실패 | safe default, forwarding off |
| 마스킹 실패 | 원문 발송 금지, `[masked due to error]` |

## 13. Privacy defaults

기본값:

```json
{
  "notificationForwarding": {
    "enabled": false,
    "mode": "BlockList",
    "allowApps": [],
    "blockApps": [
      "1Password",
      "Bitwarden",
      "Authy",
      "Microsoft Authenticator",
      "Windows Security"
    ],
    "maskingEnabled": true
  }
}
```

중요: 알림 포워딩은 기본 off로 시작하고, 사용자가 명시적으로 켠 뒤 권한을 승인해야 한다.
