# 알림 채널 결정: Telegram 봇 → Homebase Mobile 앱 푸시?

작성 2026-11-13 · 상태 **확정 — 1단계 구현(UnifiedPush 추가, Telegram은 대체 채널)** · 관련 결정 [`DECISIONS.md`](../DECISIONS.md) M-1·M-3·M-13·M-17, M-19, M-21 · 프로토콜 [`MOBILE_PROTOCOL.md`](MOBILE_PROTOCOL.md#푸시-알림-unifiedpush-m-19--m-21)

질문: "Telegram 봇 알림을 없애고 앱 푸시 알림으로 가면 어때?"

결론부터: Telegram은 지금 알림 채널이면서 유일한 명령 채널이다. 하지만 사용자가 **명령은 거의 쓰지 않고**, **Windows 알림은 웬만하면 다 받고 싶다**고 답했다(§3).
그래서 **UnifiedPush + RFC 8291 종단 암호화를 주 채널**로 삼고, Telegram은 전환 기간 동안 대체 채널로 두었다가 제거하는 것을 권장한다.
FCM과 앱 상주 연결은 기각한다.

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

## 3. 권장: (c) UnifiedPush(RFC 8291 암호화)를 주 채널로, Telegram은 전환 기간 뒤 제거

§4의 질문에 사용자가 답했다(2026-11-13).
- **Telegram 명령은 거의 쓰지 않는다.** 실험으로 넣어 본 기능이라 큰 의미가 없다.
- **Windows 알림 포워딩은 웬만한 알림을 다 받고 싶다.**

이 두 답으로 판단이 바뀐다. Telegram을 지킬 가장 큰 이유(유일한 명령 채널, §1.2)가 사라졌다.
반대로 앞으로 가장 많이 흐를 내용은 메신저·메일·OTP가 섞인 **Windows 토스트**다. 이를 Telegram 클라우드에 평문으로 보내는 것(§1.3)이 가장 큰 약점이 된다.

1. **앱 푸시는 (c) UnifiedPush로 하고, 페이로드는 RFC 8291로 암호화한다. 이를 주 채널로 삼는다.** 네 원칙을 모두 지키는 안은 이것뿐이다.
   - M-1: 운영할 서버가 없다(ntfy.sh 또는 사용자가 이미 쓰는 배포자). 키 배포도 없다. 엔드포인트는 페어링된 앱이 인증 API로 각 PC에 등록한다.
   - M-3: 앱 쪽은 라이브러리 없이 플랫폼 암호로 처리한다(커넥터 라이브러리를 쓰더라도 작다). PC 쪽 의존성도 0이다.
   - 프라이버시: 중계 서버는 암호문만 본다. 토스트 전체 포워딩이라면 **필수 조건**이다.
   - 기존 구조에 들어맞는다. `IOutboundChannel`을 하나 더하는 일이고, `DeliveryPolicy`로 채널 우선순위를 고른다.
2. **Telegram은 바로 끄지 않고, 전환 기간 동안 대체 채널로 둔 뒤 제거한다.** 전환 기간에는 UnifiedPush를 우선하고, 실패할 때만 Telegram으로 보낸다(`LocalPreferred`와 같은 방식).
   UnifiedPush가 실사용에서 놓치는 알림이 없다고 확인되면 Telegram 채널·폴링·명령 20개를 걷어 낸다.
   이때 함께 바꿔야 하는 것이 있다. 설치 마법사가 지금 Telegram 봇 토큰을 필수로 받고 Tray 연결 확인도 Telegram으로 하므로, 둘 다 선택 사항으로 바꾸고 README도 고친다.
   `/mute`처럼 남길 가치가 있는 기능은 Tray 로컬 메뉴로 옮긴다. 이렇게 하면 앱은 읽기 전용 원칙을 그대로 지킬 수 있다.
3. **토스트 포워딩을 많이 보내려면 PC 쪽에 다음이 필요하다.**
   - 기본값을 켜짐·BlockList로 하되, 비밀번호 관리자·OTP 앱 차단 목록과 마스킹은 유지한다.
   - 지금 토스트 경로는 `AlertPolicy`(중복·분당 제한)를 거치지 않는다. 그래서 앱별 묶음·속도 제한을 새로 둬야 한다.
   - ntfy.sh 익명 사용에는 버스트·일일 한도가 있고 초과하면 429를 받는다. v2.37부터 UnifiedPush 메시지는 `visitor-subscriber-rate-limiting`이 켜진 서버에서 구독자(휴대폰 IP 또는 계정) 기준으로 센다(기본값은 게시자 기준). 버스트는 문서에 따라 60건에 5초 또는 10초당 1건 보충으로 적혀 있어 일치하지 않는다.
     **실측(2026-11-13, 익명 `GET https://ntfy.sh/v1/account`, 개인정보 없이)**: `basis: "ip"`, `messages: 250`(하루), `messages_expiry_duration: 43200`(12시간 보관). 게시자 기준으로 세면 같은 공인 IP 뒤의 PC들이 이 250건을 나눠 쓴다.
     **E2E로 확인한 ntfy.sh 동작(2026-11-13)**: `?up=1` 게시는 그 토픽에 활성 구독자가 없으면 `507`(코드 50701, "cannot publish to UnifiedPush topic without previously active subscriber")로 거절된다. 즉 ntfy.sh는 UP 메시지를 **구독자(휴대폰) 기준**으로 센다.
     따라서 하루 250건은 휴대폰 한 대가 모든 PC와 나눠 쓰는 한도다. 응답에는 `RateLimit-Policy: "requests";q=60;w=300`(300초에 60건 버스트)도 붙는다.
     그래서 토스트 예산 기본값을 PC당 하루 150건으로 두어 경보 몫을 남겼다(M-21). PC가 여러 대면 `maxPushesPerDay`를 나눠 줄인다. 429에는 `Retry-After`가 오고 PC가 이를 따른다. 그동안 경보는 Telegram으로 넘어간다.
   - 페이로드는 4KB 안이어야 하므로 본문을 자른다.
   - 앱 쪽에서는 PC·앱별로 알림 채널과 그룹을 나눠, 사용자가 Android 설정에서 원하는 앱만 끌 수 있게 한다.
4. **(b) FCM은 기각한다.** 공개 사이드로드 APK에서는 서비스 계정 키를 배포하거나 중계 서버를 둬야 해서 M-1에 걸린다. Firebase 의존성으로 M-3에도 걸린다. 배터리 이점은 (c)의 FCM판 배포자로 이미 얻을 수 있다.
5. **(d) 상주 연결은 기각한다.** 상시 알림, 사용자가 직접 켜야 하는 배터리 최적화 예외(없으면 Doze 중 다운 알림이 유지보수 창까지 늦어짐), PC 수에 비례하는 배터리 비용, OEM 종료, Quick Tunnel 주소 변경, M-13 분리 작업까지 대가가 가장 크다. 내용이 망 밖으로 안 나간다는 장점은 Cloudflare 경로를 쓰는 순간 사라진다.

처음 초안에는 "PC가 ntfy 토픽에 평문으로 바로 POST해서 싸게 검증해 보자"는 단계가 있었다. 토스트 전체 포워딩이 주 용도로 확인되었으므로 **폐기한다.** 메신저 내용을 제3자 서버에 평문으로 보내게 되기 때문이다.

---

## 4. 착수 전에 사용자에게 확인할 것 (1·2는 답변 완료, §3 참고)

1. **실제로 의지하는 알림은 무엇인가?** 부팅 알림, 프로세스·서비스 다운, HTTP·포트 다운, 이벤트 로그, AI 건강 조언, Windows 토스트 포워딩 중에서 골라 달라.
   부팅·다운 알림만이라면 (c)의 범위가 작아진다. 토스트 포워딩까지라면 내용이 민감하므로 암호화가 필수다.
   → **답(2026-11-13): Windows 알림은 웬만한 것은 다 받고 싶다.** 나머지(부팅·다운·이벤트 로그·AI 조언)는 따로 말하지 않았으므로 현행대로 유지한다고 본다.
2. **Telegram 명령을 쓰는가? 쓴다면 무엇을?** 특히 `/mute`, `/update`, `/advise`, `/alerts`.
   안 쓰거나 조회만 한다면 완전 대체의 걸림돌은 알림 이력 화면 정도로 줄어든다. 상태 변경 명령을 쓴다면 Telegram을 유지하거나 앱 명령 원칙을 개정해야 한다.
   → **답(2026-11-13): 거의 안 쓴다. 실험으로 넣어 본 기능이다.** 그래서 앱에 명령을 만들지 않고, 전환 뒤 Telegram 명령은 제거한다(§3-2).
3. (부수) 휴대폰에 배포자 앱(ntfy)을 하나 더 설치하는 것을 받아들일 수 있는가? 휴대폰에 Google Play 서비스가 있는가? 이 답으로 ntfy의 F-Droid판과 Play판 중 무엇을 쓸지 정해진다.
   → **답(2026-11-13): 설치 가능, Play 서비스 없음.** F-Droid판 ntfy를 쓰고, ntfy에 배터리 최적화 예외를 준다(§5).

---

## 5. 1단계 구현 현황 (2026-11-13)

- PC: `unifiedpush` 채널(RFC 8291, 의존성 0), `POST`/`DELETE /api/v1/push`, router 우선순위 변경, 부팅·테스트 메시지 router 경유, 토스트 기본 켜짐·앱별 묶음·일일 예산. 세부 결정은 M-21에 있다.
- 앱: 라이브러리 없는 UnifiedPush 수신, Keystore로 감싼 키, PC·앱별 알림 채널, 알림 권한 요청, ntfy 설치 안내.
- E2E(에뮬레이터 `homebase-phone-api36`, API 36, 무음): 개발용 Agent 호스트(loopback, `adb reverse`) ↔ 앱 페어링 → 배포자 REGISTER(`pi` 생성자와 API 34+ identity 공유 확인) → NEW_ENDPOINT → PC에 `POST /api/v1/push` → PC가 **실제 ntfy.sh**로 암호문 전송 → 배포자 MESSAGE → 앱이 복호화해 경보 1건과 토스트 2건(묶음 1건 포함)을 PC·앱별 채널에 표시했다. 앱에서 푸시를 끄면 PC 구독 삭제와 배포자 UNREGISTER까지 확인했다. 이 동안 샘플러는 켜지지 않았다.
  배포자는 실제 ntfy 앱이 아니라 AND_3만 구현한 테스트용 최소 배포자(스크래치, 저장소 밖)였다. ntfy 앱 자체와의 연동은 아직 검증하지 않았다.
- M-13: 푸시 등록·전송은 `LiveSnapshotHub`를 구독하지 않는다. 알림은 원래 상시로 만들어지므로 샘플러·ETW·Tray 보고가 켜지지 않는다. 등록 API 테스트에서 샘플러가 꺼져 있음을 확인한다.
- §4-3 답(2026-11-13): **ntfy 설치는 괜찮고, 휴대폰에 Google Play 서비스가 없다.** 그래서 ntfy **F-Droid판**을 쓴다.
  F-Droid판은 자체 상시 연결 하나와 상시 알림 하나를 둔다. §2 Doze 행대로 포그라운드 서비스만으로는 Doze 중 네트워크가 끊기므로, **ntfy 앱에 배터리 최적화 예외**가 필요하다.
  예외는 배포자 하나만 받으면 되고, Homebase 앱은 예외가 필요 없다. 앱은 `PowerManager.isIgnoringBatteryOptimizations(<배포자>)`로 이를 확인해, 예외가 없으면 푸시 알림 행에 안내와 설정 버튼을 띄운다. README에도 적었다.
- 2단계(Telegram 제거)와 남은 개선점은 [`BACKLOG.md`](../BACKLOG.md)에 있다.

## 근거·출처

- 코드: `src/LocalOpsBot.Agent/Services/{AlertDispatcher,BootNotificationService,NotificationForwardingService,WatchdogBackgroundService,DevMonitorBackgroundService,EventLogPollingService,TelegramPollingService}.cs`, `src/LocalOpsBot.Core/Delivery/OutboundRouter.cs`, `src/LocalOpsBot.Core/Commands/*CommandHandler.cs`, `config/appsettings.example.json`, `mobile/app/build.gradle.kts`, `mobile/app/src/main/AndroidManifest.xml`
- Android 포그라운드 서비스 시간 제한(`dataSync` 24시간당 6시간): <https://developer.android.com/develop/background-work/services/fgs/timeout>
- Doze 제한(네트워크 중단·wakelock 무시·알람 지연), 포그라운드 서비스 예외는 App Standby에만 해당, 배터리 최적화 예외 앱의 Doze 중 네트워크·partial wakelock, FCM 사용자 표시 메시지: <https://developer.android.com/training/monitoring-device-state/doze-standby>
- FCM HTTP v1은 OAuth2 액세스 토큰(서비스 계정)을 요구: <https://firebase.google.com/docs/cloud-messaging/migrate-v1>
- RFC 8291 Message Encryption for Web Push: <https://www.rfc-editor.org/rfc/rfc8291>
- ntfy 한도(버스트·일일 한도, v2.37부터 UnifiedPush를 구독자 기준으로 집계): <https://docs.ntfy.sh/releases/>, <https://docs.ntfy.sh/config/>
- "재확인" 표시 항목(ntfy E2E 현황, ntfy Play판의 FCM 사용, ntfy.sh 캐시 기간, ntfy.sh 실제 익명 한도)은 작성 시점에 1차 출처로 확인하지 못했다. (c) 착수 시 ntfy 문서로 확정한다.
