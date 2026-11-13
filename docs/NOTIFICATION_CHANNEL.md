# 알림 채널 결정: Telegram 봇 → Homebase Mobile 앱 푸시?

작성 2026-11-13 · 상태 **제안 — §4의 사용자 확인 전까지 미확정** · 관련 결정 [`DECISIONS.md`](../DECISIONS.md) M-1·M-3·M-13·M-17, M-19

질문: "Telegram 봇 알림을 없애고 앱 푸시 알림으로 가면 어때?"

결론부터: **Telegram은 지금 알림 채널이면서 유일한 명령 채널이다.** 앱 푸시는 Telegram을 대체하지 않고 **선택 채널로 추가**하는 것을 권장한다.
푸시 방식은 FCM이나 상주 연결이 아니라 **UnifiedPush + RFC 8291 종단 암호화**다(§3).
Telegram을 완전히 없앨지는 §4의 두 질문에 대한 답을 들은 뒤 정한다.

---

## 1. 지금 Telegram으로 오가는 것 (코드 기준 인벤토리)

### 1.1 PC → 휴대폰 (보내는 것)

모든 자동 알림은 `IAlertDispatcher`(`src/LocalOpsBot.Agent/Services/AlertDispatcher.cs`)를 거친다.
이 dispatcher는 `AlertPolicy`(음소거·중복·속도 제한)로 걸러서 `IOutboundRouter`로 넘기고, 결과를 SQLite 알림 로그에 기록한다.
`OutboundRouter`는 `kdeconnect`·`telegram` 두 채널 ID만 안다. 등록된 `IOutboundChannel`은 **`TelegramOutboundChannel` 하나뿐**이다.
그래서 `DeliveryPolicy`가 `Both`, `TelegramFallback`, `LocalPreferred` 중 무엇이든 결국 Telegram 한 곳으로 나간다(Device Hub 계획 §TRAIN-A의 "자동 알림 출력 불변").

| 출처 | 종류(`Kind`) · 심각도 | 경로 | 비고 |
|---|---|---|---|
| `WatchdogBackgroundService` | `process_watch`, `service_watch` · 설정값 Warning/Critical, 복구 시 Recovery | dispatcher | 상태가 바뀔 때만 보낸다. Critical은 `DeliveryPolicy.Both`다. |
| `DevMonitorBackgroundService` | `http_monitor`, `port_monitor` · Warning / Recovery | dispatcher | URL·호스트:포트와 오류가 본문에 들어간다. |
| `EventLogPollingService` | `event_log` · 이벤트 수준에 따름 | dispatcher | `eventLog.llmInterpret`가 켜져 있으면 로컬 LLM 해석을 덧붙인다(주기당 상한 있음, 기본 꺼짐). |
| `PcHealthMonitor` (`PcHealthAdvisorService`) | `advisor` · Warning | dispatcher | 임계 초과가 연속되면 로컬 LLM 조언을 보낸다. 쿨다운이 있다. |
| `BootNotificationService` | "PC 부팅됨" 1회 | **`ITelegramClient` 직접 호출** (router·policy를 거치지 않음) | 네트워크 준비를 기다리며 약 16분 재시도하고 10분 중복을 막는다. 대상은 `telegram.allowedChatIds[0]`이다. |
| `NotificationForwardingService` ← Tray `NotificationForwardingHost` (파이프 `Homebase.NotificationPipe`) | `windows-toast` · Info | `IOutboundRouter`에 `LocalPreferred`로 직접 | Windows 토스트를 전달한다. **기본 꺼짐**(`notificationForwarding.enabled: false`). 차단 목록(1Password·Authy 등)과 정규식 마스킹(6자리 OTP·password·bearer·token)이 있다. `Blocked`는 버린다. |
| Tray `ConnectionReadiness` | 설정 마법사의 테스트 메시지 | `ITelegramClient` 직접 | 설치 시 연결 확인용이다. |

알림 정책(`alerting`): 중복 창은 600초, 분당 최대 10건이다. 음소거(`/mute`)는 Critical에는 적용되지 않는다.

### 1.2 휴대폰 → PC (받는 것): 명령

`TelegramPollingService`가 Bot API `getUpdates`를 long polling(outbound HTTPS)으로 받는다.
`AllowedChatPolicy`로 허용 chat만 통과시키고, `CommandRouter`가 `ICommandHandler` 20개 중 하나로 보낸 뒤 응답을 같은 chat에 답한다.

| 분류 | 명령 | 앱 실시간 뷰로 대체되나 |
|---|---|---|
| 상태 조회 | `/status`, `/disk`, `/uptime`, `/process`, `/ping` | 대부분 된다. 앱은 CPU·RAM·디스크 I/O·창별 프로세스를 보여 준다. 단 `/process`는 "감시 대상 프로세스"의 상태다. |
| 감시 결과 조회 | `/watch`, `/services`, `/ports`, `/http`, `/dev`, `/llm` | **안 된다.** 앱은 감시 설정·결과를 보여 주지 않는다. |
| 이력·진단 | `/alerts`(SQLite 알림 로그), `/events`(이벤트 로그), `/diagnostics`, `/policy`, `/help` | **안 된다.** |
| AI | `/advise`: 로컬 LLM(Ollama)이 PC 상태를 보고 쓴 조언. LLM은 PC 안에서 돌고 **결과 텍스트만** Telegram으로 나간다. | **안 된다.** |
| **상태 변경** | `/mute <기간>`, `/unmute`, `/update`(다운로드·검증 후 자기 재시작 설치) | **안 된다.** 앱은 의도적으로 읽기 전용이다(DECISIONS "원격 명령은 이 앱 범위가 아니다", ADR-005). |

`IRemoteCommandRouter`/`LegacyRemoteCommandRouter`(`CommandPrincipalKind.PairedDevice` 포함)는 등록만 되어 있고 배선되지 않았다(Device Hub Phase-1 의도). 즉 **Telegram 말고는 명령이 들어올 길이 없다.**

### 1.3 이미 Telegram 클라우드를 지나는 것 (프라이버시 기준선)

Telegram 봇 대화는 비밀 대화가 아니라 클라우드 채팅이라 **종단 암호화가 아니다.** 위 알림 본문, 명령 응답(`/status`의 PC 이름·사용량, `/advise` 조언 텍스트), 포워딩을 켰다면 마스킹된 Windows 토스트까지 Telegram 서버가 평문으로 볼 수 있다.
봇 토큰은 각 PC의 `ProgramData` 설정에 있다.
따라서 "앱 푸시로 바꾸면 내용이 외부로 안 나간다"는 비교의 기준점은 **지금도 외부(Telegram)로 나가고 있다**는 사실이다.

### 1.4 Homebase Mobile의 현재 상태 (PR #6·#8 기준)

- 알림 코드는 없다. `POST_NOTIFICATIONS` 권한, 알림 채널, 서비스, WorkManager 모두 없다. 연결은 화면이 STARTED일 때만 연다(M-13).
- 의존성은 Compose, OkHttp, zxing뿐이다(M-3). 배포는 GitHub Releases 사이드로드다(M-4).
- 경로는 LAN(TLS 핀) / Tailscale / Cloudflare Tunnel이며 QR에서 자동으로 고른다(M-15~M-18). 그래서 **"Tailscale이 꺼지면 앱이 PC에 못 붙는다"는 이제 Cloudflare를 켠 경우에는 사실이 아니다.**

---

## 2. 네 가지 안 비교

공통 전제: 알림을 *만드는* 쪽(watchdog·event log·advisor)은 휴대폰 연결과 무관하게 이미 상시로 돈다. 그래서 (a)(b)(c)는 M-13("보고 있을 때만 수집")과 충돌하지 않는다.
(d)만 충돌 위험이 있다(아래 표 참고).

| 항목 | (a) Telegram 유지 | (b) FCM 앱 푸시 | (c) UnifiedPush / ntfy | (d) 앱 포그라운드 서비스 + PC별 상주 연결 |
|---|---|---|---|---|
| **휴대폰 의존성** | 없음(Telegram 앱) | `firebase-messaging`(+ Google Play 서비스 필수, `google-services.json`, Gradle 플러그인). **M-3 위반** | 배포자 앱(ntfy 등) 설치 필요. Homebase 쪽은 UnifiedPush 수신 규약(브로드캐스트 인텐트) + 플랫폼 암호(ECDH P-256·HKDF·AES-GCM)로 **라이브러리 없이** 가능. 커넥터 라이브러리를 써도 됨 | 없음(OkHttp WebSocket 재사용) |
| **PC 의존성** | 현행 | FCM HTTP v1: 서비스 계정 키로 OAuth2 토큰을 발급해야 함(legacy 서버 키는 2024-07 종료) | WebPush 암호화(RFC 8291) + HTTP POST. .NET `System.Security.Cryptography`로 의존성 0 | 현행 서버에 알림 전용 스트림 추가 |
| **서버·키** | Telegram 서버, PC마다 봇 토큰 | Google 서버 + **Firebase 프로젝트와 서비스 계정 키**. 공개 APK는 하나의 Firebase 프로젝트에 묶이므로, 남의 PC가 내 앱 사용자에게 보내려면 그 서비스 계정 키를 나눠 주거나 **중계 서버**를 운영해야 함 → **M-1(서버리스) 위반**. 사용자마다 자기 Firebase로 다시 빌드하는 방법만 남는데, 이는 사이드로드 배포 모델(M-4)과 맞지 않음 | ntfy.sh(공개, 계정 없음): 제3자 서버이지만 키 배포는 없음. 엔드포인트 URL이 곧 비밀. **자체 호스팅**: 휴대폰이 밖에서 닿아야 하므로 VPS(=M-1 위반) 또는 tailnet 내부(=Tailscale 필요)가 됨 | 없음. PC가 서버 |
| **프라이버시** (내용이 망 밖으로 나가나) | **나간다.** Telegram 서버가 평문으로 봄(§1.3) | 앱이 페이로드를 암호화해 data 메시지로 보내면 Google은 메타데이터만 봄(4KB 제한). 평문 notification 메시지는 Google이 봄 | RFC 8291이면 **ntfy 서버는 암호문만 봄**(UnifiedPush 페이로드는 RFC 8291 암호화된 POST 본문). ntfy 일반 게시(앱 연동 없이 토픽에 POST)는 서버에서 평문(ntfy 자체 E2E는 issue #69로 오래 열려 있음, 채택 전 재확인) | **나가지 않는다**(LAN·tailnet은 종단 암호화). 단 Cloudflare 경로는 Cloudflare 엣지에서 TLS가 끝나므로 Cloudflare가 평문을 볼 수 있음(M-17과 같은 성격) |
| **배터리** | 추가 0(Telegram 앱이 이미 FCM으로 받음) | 추가 거의 0(시스템 FCM 연결 공유) | ntfy F-Droid판은 자체 상주 연결(포그라운드 서비스) 1개를 모든 앱이 공유. Play판은 ntfy.sh 토픽에 FCM으로 깨움(ntfy 문서 기준, 재확인). 어느 쪽이든 Homebase가 따로 쓰는 배터리는 없음 | **가장 큼.** PC 수만큼 소켓, 하트비트(Cloudflare 경로는 유휴 타임아웃 때문에 필수). 측정 전 수치 없음. Samsung·Xiaomi 등 OEM 백그라운드 종료에 취약 |
| **Doze** | 문제 없음 | 사용자에게 보이는 우선순위 높은 메시지는 Doze 중에도 전달되고, 앱은 잠시 네트워크·partial wakelock을 받음 | 배포자 몫. FCM판은 문제 없음. 자체 연결판은 배포자 앱이 **배터리 최적화 예외**를 받아야 Doze 중에도 연결을 유지함(포그라운드 서비스만으로는 안 됨). 예외는 배포자 하나만 받으면 됨 | **포그라운드 서비스만으로는 Doze 예외가 아님.** 공식 문서상 포그라운드 서비스는 App Standby만 막는다. Doze는 네트워크를 끊고 wakelock을 무시하며 일반 알람을 유지보수 창까지 미룬다. Doze 중 네트워크와 partial wakelock을 쓰려면 **배터리 최적화 예외**가 있어야 한다. 사용자가 설정에서 직접 켜야 하고(사이드로드라 Play 정책 제한은 없음), 앱은 `isIgnoringBatteryOptimizations()`로 확인해 안내해야 한다. 하트비트는 `setExactAndAllowWhileIdle` 알람이 필요하다(예외 앱도 일반 알람은 Doze 중 울리지 않음). OEM 절전은 별도로 실기기 검증이 필요하다. 덧붙여 targetSdk 35에서 `dataSync` 유형은 **24시간에 6시간 한도**라 쓸 수 없고 `specialUse`/`connectedDevice` 유형이 필요하다(OS 선행 조건 적용) |
| **상시 알림** | 없음 | 없음 | ntfy 자체 연결판은 ntfy가 상시 알림 1개를 띄움 | **Homebase가 상시 알림 1개** 필요(Android 13+에서는 사용자가 밀어서 지울 수 있지만 서비스는 남음) |
| **Android 13+ 알림 권한** (`POST_NOTIFICATIONS`) | Telegram 앱이 이미 보유 | Homebase에 새로 필요(런타임 요청) | Homebase에 새로 필요(+ 배포자 앱에도) | Homebase에 새로 필요 |
| **Tailscale이 꺼졌을 때** | 동작 | 동작 | ntfy.sh: 동작. tailnet 자체 호스팅: **불가** | 집 Wi-Fi(LAN)이면 동작, 밖이면 Cloudflare를 켠 PC만 동작. Quick Tunnel은 PC 재시작마다 주소가 바뀌어(M-17) 다시 붙기 전까지 끊김. Tailscale-only 구성은 **불가** |
| **휴대폰이 꺼져 있었을 때** | Telegram이 보관. 대화가 곧 이력 | FCM이 TTL 동안 보관(기본 최대 4주) | ntfy 서버 캐시(ntfy.sh 기본 12시간, 재확인) | 보관 없음. 재연결 시 PC 알림 로그(`/alerts`의 SQLite)를 받아 오는 API가 별도로 필요 |
| **명령(§1.2)** | 그대로 | 없음. 앱에 명령 UI와 인증된 명령 API를 새로 만들어야 함(앱 읽기 전용 원칙 개정) | 없음(같음) | 없음(같음). 연결은 있지만 명령은 같은 원칙 개정이 필요 |
| **M-13** | 무관 | 무관 | 무관 | **위반 위험.** 기존 `WS /api/v1/live`에 붙으면 샘플러·ETW·Tray 보고가 상시로 켜진다. 구독자 수에 넣지 않는 알림 전용 스트림을 따로 만들어야 함 |
| **구현 크기** (추정) | 0 | 앱: Firebase 연동, 토큰 등록, 알림 표시. PC: OAuth2 JWT 서명·FCM 채널. **+ 키 배포 또는 중계 서버.** 중~대 | 앱: 배포자 등록, 엔드포인트를 PC마다 등록(`POST /api/v1/push` 같은 인증 API), 복호화, 알림 채널·권한. PC: `IOutboundChannel` 구현(RFC 8291 암호화 약 200줄), 기기별 엔드포인트 저장(M-11 `state.json`), `OutboundRouter`가 채널 ID 두 개를 하드코딩한 부분 수정, `BootNotificationService`를 router 경유로 전환. 중 | 앱: 포그라운드 서비스, 배터리 최적화 예외 안내·확인, 재연결·백오프, 유휴 중 알람 하트비트, 다중 PC, 알림. PC: 알림 전용 스트림, 놓친 알림 재전송. **+ Doze(`adb shell dumpsys deviceidle force-idle`)·OEM 절전 실기기 검증.** 중~대, 유지 비용 큼 |

§1에서 나온 공통 작업이 있다. **어떤 앱 푸시안이든** `BootNotificationService`와 Tray 테스트 메시지가 Telegram을 직접 부르고 있으므로 router를 거치도록 바꿔야 한다.
또 Telegram을 끄면 `/alerts` 같은 이력 조회 수단이 사라지므로, 앱에 알림 이력 화면이 필요해진다.

---

## 3. 권장: (a) 유지 + (c) UnifiedPush를 선택 채널로 추가

1. **Telegram은 당분간 유지한다.** 유일한 명령 채널이고(§1.2의 20개 중 `/mute`·`/update`·`/advise`·감시 조회는 대체 수단이 없음), 추가 배터리·서버·의존성이 0이다.
   프라이버시가 약한 것(§1.3)이 바꿀 이유가 될 수는 있지만, 그 이유라면 아래 (c)로 해결된다.
2. **앱 푸시가 필요하면 (c) UnifiedPush를 쓰고, 페이로드는 RFC 8291로 암호화한다.** 이것만 네 원칙을 모두 지킨다.
   - M-1: 운영할 서버가 없다(ntfy.sh 또는 사용자가 이미 쓰는 배포자). 키 배포도 없다. 엔드포인트는 페어링된 앱이 인증 API로 각 PC에 등록한다.
   - M-3: 앱 쪽은 라이브러리 없이 플랫폼 암호로 처리한다(커넥터 라이브러리를 쓰더라도 작다). PC 쪽 의존성도 0이다.
   - 프라이버시: 중계 서버는 암호문만 본다. **지금의 Telegram보다 낫다.**
   - 기존 구조에 들어맞는다. `IOutboundChannel`을 하나 더하는 일이고, `DeliveryPolicy`로 채널 우선순위를 고를 수 있다.
3. **(b) FCM은 기각한다.** 공개 사이드로드 APK에서는 서비스 계정 키를 배포하거나 중계 서버를 둬야 해서 M-1에 걸린다. Firebase 의존성으로 M-3에도 걸린다. 배터리 이점은 (c)의 FCM판 배포자로 이미 얻을 수 있다.
4. **(d) 상주 연결은 기각한다.** 상시 알림, 사용자가 직접 켜야 하는 배터리 최적화 예외(없으면 Doze 중 다운 알림이 유지보수 창까지 늦어짐), PC 수에 비례하는 배터리 비용, OEM 종료, Quick Tunnel 주소 변경, M-13 분리 작업까지 대가가 가장 크다. 내용이 망 밖으로 안 나간다는 장점은 Cloudflare 경로를 쓰는 순간 사라진다.
5. **완전 대체는 명령 문제가 풀린 뒤에 다시 판단한다.** Telegram을 끄려면 앱에 ① 알림 이력 화면, ② 최소 명령(`/mute`·`/unmute`, 필요하면 `/update`)이 있어야 한다.
   ②는 "앱은 읽기 전용" 원칙(ADR-005와의 관계 포함)을 바꾸는 일이므로 따로 결정한다.

**싼 검증부터.** 엔지니어링을 쌓기 전에, PC가 ntfy 토픽으로 바로 POST하는 최소 채널(앱 수정 없음, ntfy 앱으로 받음)로 1~2주 써 보는 방법이 있다.
이렇게 하면 "Telegram 대신 일반 푸시를 실제로 더 원하는가"를 확인할 수 있다.
단 이 방식은 ntfy.sh에서 평문이므로 프라이버시는 Telegram과 같은 수준이다. 검증용으로만 쓰고 본 구현은 암호화된 UnifiedPush로 한다.

---

## 4. 착수 전에 사용자에게 확인할 것

1. **실제로 의지하는 알림은 무엇인가?** 부팅 알림, 프로세스·서비스 다운, HTTP·포트 다운, 이벤트 로그, AI 건강 조언, Windows 토스트 포워딩 중에서 골라 달라.
   부팅·다운 알림만이라면 (c)의 범위가 작아진다. 토스트 포워딩까지라면 내용이 민감하므로 암호화가 필수다.
2. **Telegram 명령을 쓰는가? 쓴다면 무엇을?** 특히 `/mute`, `/update`, `/advise`, `/alerts`.
   안 쓰거나 조회만 한다면 완전 대체의 걸림돌은 알림 이력 화면 정도로 줄어든다. 상태 변경 명령을 쓴다면 Telegram을 유지하거나 앱 명령 원칙을 개정해야 한다.
3. (부수) 휴대폰에 배포자 앱(ntfy)을 하나 더 설치하는 것을 받아들일 수 있는가? 휴대폰에 Google Play 서비스가 있는가? 이 답으로 ntfy의 F-Droid판과 Play판 중 무엇을 쓸지 정해진다.

---

## 근거·출처

- 코드: `src/LocalOpsBot.Agent/Services/{AlertDispatcher,BootNotificationService,NotificationForwardingService,WatchdogBackgroundService,DevMonitorBackgroundService,EventLogPollingService,TelegramPollingService}.cs`, `src/LocalOpsBot.Core/Delivery/OutboundRouter.cs`, `src/LocalOpsBot.Core/Commands/*CommandHandler.cs`, `config/appsettings.example.json`, `mobile/app/build.gradle.kts`, `mobile/app/src/main/AndroidManifest.xml`
- Android 포그라운드 서비스 시간 제한(`dataSync` 24시간당 6시간): <https://developer.android.com/develop/background-work/services/fgs/timeout>
- Doze 제한(네트워크 중단·wakelock 무시·알람 지연), 포그라운드 서비스 예외는 App Standby에만 해당, 배터리 최적화 예외 앱의 Doze 중 네트워크·partial wakelock, FCM 사용자 표시 메시지: <https://developer.android.com/training/monitoring-device-state/doze-standby>
- FCM HTTP v1은 OAuth2 액세스 토큰(서비스 계정)을 요구: <https://firebase.google.com/docs/cloud-messaging/migrate-v1>
- RFC 8291 Message Encryption for Web Push: <https://www.rfc-editor.org/rfc/rfc8291>
- "재확인" 표시 항목(ntfy E2E 현황, ntfy Play판의 FCM 사용, ntfy.sh 캐시 기간)은 작성 시점에 1차 출처로 확인하지 못했다. (c) 착수 시 ntfy 문서로 확정한다.
