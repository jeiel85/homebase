# 16. Design Supplement — 보완 설계서

이 문서는 `00`~`15` 설계서에서 미비했던 4가지 영역을 보강한다.

---

## 1. Toast 포워딩 실현 가능성 검증

### 1.1 결론: 실현 가능, 단 다음 조건 필요

Windows Toast 알림 읽기는 `Windows.UI.Notifications.Management.UserNotificationListener` WinRT API를 통해 가능하다. 단, unpackaged WPF 앱에서 사용할 때 다음 제약이 있다.

### 1.2 사용 API

```
네임스페이스: Windows.UI.Notifications.Management
클래스:      UserNotificationListener
메서드:
  - static UserNotificationListener Current { get; }
  - Task<UserNotificationListenerAccessStatus> RequestAccessAsync()
  - Task<IReadOnlyList<UserNotification>> GetNotificationsAsync(NotificationKinds kinds)
```

각 `UserNotification`이 제공하는 정보:

| 속성 | 타입 | 설명 |
|---|---|---|
| Id | uint | 시스템 내 고정 ID |
| AppInfo | AppInfo | 알림을 보낸 앱 (DisplayInfo, PackageFamilyName 등) |
| Notification | UserNotificationContent | 본문 (text, image 등) |
| CreationTime | DateTimeOffset | 알림 생성 시간 |

`UserNotificationContent`는 `TextProperties` 컬렉션을 가지며, Toast의 제목/본문에 해당하는 텍스트를 포함한다.

### 1.3 WPF (unpackaged) 호환성

| 조건 | 상태 | 근거 |
|---|---|---|
| .NET TFM | `net9.0-windows10.0.17763.0` 이상 (설치된 SDK 기준) | CsWinRT 내장 프로젝션 사용 |
| NuGet 필요 | 없음 (TFM만 맞추면 됨) | WinRT API는 OS에 내장 |
| 앱 매니페스트 | unpackaged는 불필요 | `RequestAccessAsync()` 런타임 동의로 대체 |
| UI 스레드 | STA 필요 | WPF는 기본 STA, `RequestAccessAsync()`는 UI 스레드에서 호출 |
| 최소 Windows 버전 | 10.0.17763 (1809) 이상 | UserNotificationListener는 1809부터 unpackaged 앱 지원 |

### 1.4 Permission Flow 상세

```text
1. 사용자가 Tray App 실행
2. Tray가 UserNotificationListener.Current.RequestAccessAsync() 호출 (UI 스레드)
3. Windows 시스템 권한 대화상자 표시
4. 사용자 선택
   ┌─ Allow: UserNotificationListenerAccessStatus.Allowed → 포워딩 시작
   └─ Deny:  UserNotificationListenerAccessStatus.Denied  → 기능 비활성화, 재시도 버튼 제공
5. 권한 취소 시: Windows 설정 > 접근성 > 알림 수신기 에서 변경 가능
   → Tray는 주기적으로 access status 재확인 (10분 간격)
```

### 1.5 읽기 전략 (기존 설계 보강)

**Strategy A: Periodic read (채택)**

- `listener.GetNotificationsAsync(NotificationKinds.Toast)` 3초 간격 호출
- 모든 알림 목록을 읽은 뒤 `Id` 기준으로 새로운 알림만 선별
- 과거에 본 Id는 `HashSet<uint>`로 추적 (메모리)
- 정합성 유지를 위해 매 5분마다 최대 1000개까지 `HashSet<uint>` 보관

**Strategy A가 채택된 이유**

- `UserNotificationListener`는 event-driven 대안이 없음
- `GetNotificationsAsync`는 단순 주기 폴링 API만 제공
- 3초 폴링이 CPU/배터리에 미치는 영향 무시할 수준 (단순 ID 비교)

### 1.6 알림 내용 추출 로직

```text
UserNotification notification = ...;
UserNotificationContent content = notification.Notification.Visual.GetBinding();
IReadOnlyList<AdaptiveNotificationText> texts = content.GetTextElements();

// Toast 제목 = texts[0]?.Text
// Toast 본문 = texts[1]?.Text + texts[2]?.Text 등
// 앱 이름 = notification.AppInfo.DisplayInfo.DisplayName
```

주의: 모든 Toast가 동일한 구조를 가지지 않는다. 텍스트 배열 길이와 순서는 앱마다 다를 수 있으므로 null-safe 접근이 필수다.

### 1.7 실현 불가능 케이스 명시

다음 알림은 **Windows Notification Listener로 읽을 수 없다**:

- 앱 내부에서만 표시되고 Windows Toast 시스템을 거치지 않은 알림
- Windows Security의 상세 위협 정보 (OS 정책상 마스킹됨)
- 시스템 트레이 영역의 툴팁 (Toast 아님)
- Windows 10 미만 환경

### 1.8 Tray 프로젝트 TFM 및 참조

```xml
<TargetFramework>net9.0-windows10.0.17763.0</TargetFramework>
<UseWPF>true</UseWPF>
```

NuGet 패키지 추가 불필요.

---

## 2. IPC 결정 — Named Pipe 채택

### 2.1 결정: Named Pipe (primary), File queue는 제거

```
Tray → Pipe → Agent
```

File queue fallback을 제거하는 이유:

| 항목 | File queue | Named Pipe |
|---|---|---|
| 동시성 | race condition 발생 가능 | 커널이 직렬화 보장 |
| 지연 | 디스크 I/O + 폴링 | 즉시 전달 |
| 정리 | orphan file 누적 가능 | OS가 자동 정리 |
| 구현 복잡도 | 고려할 엣지 케이스 많음 | 단방향 스트림으로 단순 |
| 의존성 | 디렉토리 권한 필요 | 없음 |

**결론**: 추가 난이도가 유의미하게 높지 않으므로 Named Pipe로 단일화한다.

### 2.2 Pipe 이름 및 접근

| 항목 | 값 |
|---|---|
| Pipe name | `\\.\pipe\LocalOpsBot.NotificationPipe` |
| Server (생성) | Agent (Windows Service) |
| Client (연결) | Tray (User Session App) |
| 방향 | 단방향 (Tray → Agent) |
| 보안 | `PipeSecurity`로 SYSTEM+Admin+현재 사용자만 접근 |
| 전송 | `PipeTransmissionMode.Byte` (스트림) |

### 2.3 프로토콜

메시지는 **length-prefixed JSON**을 사용한다:

```text
[4바이트 little-endian 메시지 길이][UTF-8 JSON 본문]
```

JSON 스키마:

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

`sensitivity` 필드:

| 값 | 의미 |
|---|---|
| `Normal` | 필터 통과 시 전송 |
| `Sensitive` | 마스킹 필수 |
| `Blocked` | Tray에서 차단되었으나 로그용으로 전달 |

Agent는 Tray에서 받은 notification에 대해:

1. `eventId`로 중복 확인 (Agent 측 dedup 캐시 30분)
2. 마스킹 재적용 (defense in depth)
3. 알림 정책 평가 (mute/rate limit)
4. Telegram 발송
5. SQLite 저장

### 2.4 파이프 연결 수명

```text
1. Tray 시작
2. Agent 파이프 연결 시도 (최대 3초 timeout, 5회 재시도, 1초 간격)
3. 성공: 연결 유지, 메시지 전송 시마다 Write
4. 실패: 15초 후 재연결
5. Agent 재시작 감지: 연결 끊김 → 3초 후 재연결
```

### 2.5 Agent 측 IPC 서버

```csharp
public sealed class NotificationBridgePipeServer : INotificationBridgeServer, IHostedService
{
    // StartAsync:
    //   - NamedPipeServerStream 생성 (\\.\pipe\LocalOpsBot.NotificationPipe)
    //   - PipeSecurity: Allow SYSTEM, Admin, 현재 사용자
    //   - BeginWaitForConnection (비동기, 단일 연결)
    //   - 연결 수립 후 stream.Read loop (length-prefix → JSON deserialize)
    //
    // StopAsync:
    //   - pipe.Dispose()
    //   - 연결 종료
}
```

---

## 3. 테스트 전략 보강 — Infrastructure Mocking

### 3.1 계층별 테스트 가능성

| 계층 | 테스트 방식 | 모의 대상 |
|---|---|---|
| Core | Pure unit test (no mock) | 없음 |
| Core.Commands | Model/Formatter test | 없음 |
| Core.Alerts | Policy test | 없음 |
| Infrastructure.Telegram | Mock HttpMessageHandler | HTTP |
| Infrastructure.Windows | Test hooks interface | WMI/COM/WinRT |
| Data | Temp SQLite file | 없음 |
| Agent | Fake all Infrastructure | 모든 infra |
| Tray | UI automation + mock pipe | WinRT, Named Pipe |

### 3.2 Infrastructure 계층 모의 전략

핵심 원칙: **Infrastructure는 모두 interface 뒤에 숨기고, interface에는 test hook을 포함하지 않는다.** 대신 테스트 전용 구현체를 만든다.

#### 3.2.1 텔레그램

```csharp
// Tests 내부
internal sealed class FakeTelegramClient : ITelegramClient
{
    public List<(long ChatId, string Text, TelegramSendOptions? Options)> Sent { get; } = new();
    public Queue<IReadOnlyList<TelegramUpdate>> UpdateQueue { get; } = new();

    public Task SendMessageAsync(long chatId, string text, TelegramSendOptions? options, CancellationToken ct)
    {
        Sent.Add((chatId, text, options));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long? offset, int timeoutSeconds, CancellationToken ct)
    {
        return Task.FromResult(UpdateQueue.TryDequeue(out var result) ? result : Array.Empty<TelegramUpdate>());
    }
}
```

#### 3.2.2 Windows Metrics

```csharp
// Tests 내부
internal sealed class FakeSystemMetricsCollector : ISystemMetricsCollector
{
    public CollectorResult<SystemMetricSnapshot> NextResult { get; set; }
        = CollectorResult<SystemMetricSnapshot>.Ok(
            new SystemMetricSnapshot(
                DateTimeOffset.UtcNow, 14.0, 34L * 1024 * 1024 * 1024,
                20L * 1024 * 1024 * 1024, 35.0, TimeSpan.FromDays(3),
                "TEST-PC", "Windows 11"), DateTimeOffset.UtcNow);

    public Task<CollectorResult<SystemMetricSnapshot>> CollectAsync(CancellationToken ct)
        => Task.FromResult(NextResult);
}
```

#### 3.2.3 Windows EventLog

```csharp
// Tests 내부
internal sealed class FakeEventLogWatcher : IEventLogWatcher
{
    public List<WindowsEventLogItem> Items = new();

    public Task<IReadOnlyList<WindowsEventLogItem>> GetRecentEventsAsync(
        IReadOnlyList<string> logNames, IReadOnlyList<string> levels,
        TimeSpan lookback, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<WindowsEventLogItem>>(Items);
}
```

#### 3.2.4 WinRT UserNotificationListener

Tray App의 Toast 알림 읽기는 통합 테스트 수준에서만 검증하고, 단위 테스트는 `INotificationBridgeClient`와 `INotificationFilter`/`ITextMasker`로 분리하여 검증한다.

### 3.3 테스트 적용 범위 확장

기존 `13_testing_plan.md`에 다음을 추가:

**TelegramPollingService 통합 테스트 (Agent 수준)**

```text
- FakeTelegramClient + FakeCommandRouter 조합
- offset이 정상적으로 증가하는지 검증
- polling 실패 후 backoff 후 재시작 검증
- unauthorized chat_id가 routing되지 않는 검증
```

**Infrastructure 수집 실패 시나리오**

```text
- FakeSystemMetricsCollector.NextResult를 Fail로 설정
- /status 응답에서 해당 항목만 "unknown" 표시 검증
- Telegram 오류 응답에는 null 출력 없이 정상 포맷 유지
```

---

## 4. 설정 파일 구조 개선

### 4.1 파일 분리 (v1 — GOAL-00 기준)

현재 단일 `appsettings.json`을 다음 3개 파일로 분리:

| 파일 | 위치 | 내용 | Git |
|---|---|---|---|
| `appsettings.json` | `%ProgramData%/LocalOpsBot/config/` | 전체 설정, 단 botToken은 "ENV:..." 형태 | 포함 금지 (설치 시 생성) |
| `appsettings.secrets.json` | `%ProgramData%/LocalOpsBot/config/` | 실제 token, chat_id (DPAPI 암호화 예정) | 포함 금지 |
| `appsettings.example.json` | `%ProgramFiles%/LocalOpsBot/Agent/` | 예시만 포함, token은 dummy | 포함 |

초기 구현에서는 `secrets.json`을 분리하지 않고 환경변수 기반 `ENV:` prefix로 처리한다. 이 방식은 단순하며 git에 secret이 커밋될 위험을 원천 차단한다.

### 4.2 Config loading 우선순위

```text
1. 환경변수 (LOCALOPSBOT__{SECTION}__{KEY})
2. %ProgramData%/LocalOpsBot/config/appsettings.json
3. %ProgramData%/LocalOpsBot/config/appsettings.secrets.json  (v2, DPAPI)
4. 실행 파일 경로의 appsettings.json (포함된 default)
```

.NET `IConfiguration`이 `ENV:` prefix를 지원하지 않으므로, custom `ConfigurationBuilder` 확장을 추가한다.

### 4.3 ENV: Prefix Resolver

```csharp
// Infrastructure/Configuration/EnvPrefixConfigurationSource.cs
public sealed class EnvPrefixConfigurationProvider : ConfigurationProvider
{
    public override void Load()
    {
        // 모든 키-값 쌍을 탐색하며 값이 "ENV:..."면 환경변수 조회
    }
}
```

사용 방식:

```json
{
  "telegram": {
    "botToken": "ENV:LOCALOPSBOT_TELEGRAM_TOKEN"
  }
}
```

### 4.4 Config hot reload (v2 — GOAL-12 이후)

Hot reload가 필요한 설정만 별도 분리:

```json
{
  "alerting": { /* hot reload 대상 */ },
  "processWatches": [],  /* hot reload 대상 */
  "serviceWatches": [],  /* hot reload 대상 */
  "notificationForwarding": { /* hot reload 대상 */ },
  "eventLog": { /* hot reload 대상 */ }
}
```

`telegram.botToken`과 `telegram.allowedChatIds`는 서비스 재시작 필요 (hot reload 제외).

.NET `IOptionsMonitor`와 `IConfiguration.GetReloadToken()` 사용:

```csharp
builder.Services
    .AddSingleton<IConfiguration>(sp => sp.GetRequiredService<IConfigurationRoot>())
    .Configure<AlertingOptions>(config.GetSection("alerting"))
    .AddSingleton<IOptionsChangeTokenSource<AlertingOptions>>(
        sp => new ConfigurationChangeTokenSource<AlertingOptions>(config));
```

v2에서 `appsettings.json` 파일 변경 감시를 위해 `FileConfigurationSource.ReloadOnChange = true`를 활성화한다.

### 4.5 DPAPI Secrets (v3 — 장기 목표)

```powershell
# 설치 스크립트에서 token 암호화 저장
$token = Read-Host "Enter Telegram Bot Token" -AsSecureString
$encrypted = [System.Security.Cryptography.ProtectedData]::Protect(
    [System.Text.Encoding]::UTF8.GetBytes($token),
    $null,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
[System.IO.File]::WriteAllBytes("$env:ProgramData\LocalOpsBot\config\token.encrypted", $encrypted)
```

Agent는 `DPAPI:C:\ProgramData\LocalOpsBot\config\token.encrypted` 형식을 읽어 복호화한다.

---

## 5. 설계 변경 요약

| 항목 | 기존 설계 | 보완 후 |
|---|---|---|
| Toast Notification API | 추상적 API 명시 | `UserNotificationListener` WinRT 구체화 |
| Toast 읽기 전략 | A/B 전략 제시 | A 채택, 폴링/ID 추적 상세화 |
| IPC | Named Pipe or File queue | Named Pipe 확정, File queue 제거 |
| IPC 프로토콜 | JSON 미명세 | length-prefixed JSON, schemaVersion |
| Config 파일 | 단일 appsettings.json | 3단계 분할 (env/설정/비밀) |
| Config hot reload | 없음 | 파일 변경 감시 (v2) |
| Test Infrastructure mock | 전략 없음 | Fake 구현체 템플릿 제공 |
| Tray TFM | 명시 안 됨 | `net9.0-windows10.0.17763.0` |

---

## 6. 영향받는 문서

| 문서 | 변경 사항 |
|---|---|
| `01_architecture.md` IPC 섹션 | Named Pipe 단일화로 갱신 |
| `06_tray_notification_forwarder_design.md` | UserNotificationListener API 구체화, IPC 프로토콜 보강 |
| `10_class_interface_spec.md` | `INotificationBridge` 계열 시그니처 확정 |
| `09_config_security.md` | secrets.json 분리, DPAPI 계획 추가, `ENV:` resolver |
| `13_testing_plan.md` | Fake 구현체 템플릿, Infrastructure mock 전략 추가 |
| `12_installation_operation.md` | config hot reload 운영 절차 추가 |
