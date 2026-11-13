# Homebase Device Hub — 구현 계획

> 상태: **TRAIN-A Phase-1 완료 · v0.13.1 릴리스 (2026-07-15). 재개점: Phase-2 (DB V2, GOAL-05)**
> 근거 설계: [`docs/homebase-connect-design-bundle/`](homebase-connect-design-bundle/)
> 작성일: 2026-07-15 · 대상 브랜치: `main` (net9.0)

이 문서는 `homebase-connect-design-bundle` 설계 묶음을 **실제 저장소 코드와 1:1로 대조 검증**한 뒤, 실행 가능한 단위로 재정리한 구현 계획이다. 설계서를 그대로 옮긴 것이 아니라, 실측으로 확인된 사실·발견한 이슈·미결정 사항을 함께 담는다.

---

> **2026-11-13 추가 — Homebase Mobile(모니터링 전용 앱)과의 관계**: 다중 PC 실시간 모니터링 Android 앱(`mobile/`)을 별도로 도입했다.
> ADR-006을 "모니터링 뷰어는 전용 앱, 기기 연동은 KDE Connect"로 개정했다. 새 파이프 `Homebase.MobileBridge`는 TRAIN-B SessionBridge의 좁은 선행 조각이다.
> 기기 저장소(`ProgramData\Homebase\mobile\state.json`)는 GOAL-05 이후 `device` 테이블로 옮긴다. 상세는 [`DECISIONS.md`](../DECISIONS.md) M 섹션을 본다.

## 1. 목표와 원칙

**제품 방향**: Telegram 중심 모니터링 봇 → Windows PC를 중심으로 Telegram·Android(KDE Connect)·로컬 세션을 잇는 개인 **Device Hub**. 핵심은 기능 추가가 아니라 **채널(transport)과 도메인 로직의 분리**다.

**불변 원칙**
- **TRAIN-A가 끝날 때까지 사용자 가시 동작(기존 Telegram UX)은 바뀌지 않는다.** (예외: §4의 알림 포워딩 포맷 — 의도적 수용)
- KDE 프로토콜은 **실제 Android 기기로 wire를 잠그기 전에는 구현하지 않는다.**
- 원격 명령은 raw shell 금지, 서명된 `CommandDefinition` / `IRemoteCommandHandler`로만.
- 모든 신기능은 config에서 기본 `enabled: false` (안전 기본값).

**작업 열차 구조**

| TRAIN | 범위 | GOAL | 상태 |
|---|---|---|---|
| A. Foundation | Protocol·Outbound 라우팅·명령 추상화·DB V2 | 00–05 | **킷 검증 완료, 즉시 착수 가능** |
| B. Local Session | Agent↔Tray 양방향 IPC (shadow mode) | 06 | 설계 확정, 코딩 전 스키마 lock 필요 |
| C. KDE 호환 | conformance spike→discovery/pairing/battery/device UI | 07–10 | **protocol-lock 게이트 미충족** |
| D. User Features | 알림·공유·파일·명령·미디어·클립보드·릴리스 | 11–18 | B·C에 의존 |

---

## 2. 코드베이스 대조 검증 결과 (실측)

설계서 baseline(2026-07-14)이 **현재 코드와 정확히 일치**함을 파일 단위로 확인했다. 이 킷은 실제 이 저장소를 보고 작성됐다.

| 검증 항목 | 결과 | 근거 |
|---|---|---|
| 타깃 프레임워크 | 전 프로젝트 `net9.0` | 각 `.csproj` |
| `AlertDispatcher`의 `ITelegramClient`/`IOptions<TelegramOptions>` 직접 의존 | 존재, 교체 대상과 일치 | `src/LocalOpsBot.Agent/Services/AlertDispatcher.cs` |
| `NotificationForwardingService`의 Telegram 직접 의존 | 존재, 교체 대상과 일치 | 동 디렉터리 |
| `AlertEvent`(8필드)·`ToastNotificationEvent`(7필드) | 킷 교체 코드가 쓰는 필드와 완전 일치 | `Core/Alerts`, `Core/Notifications` |
| `CommandResult(Success, ResponseText, SendResponse, Error)` | `LegacyRemoteCommandRouter`가 참조하는 시그니처와 일치 | `Core/Commands/ICommandHandler.cs:10` |
| `ToLog(...)` 12인자 | 교체 `AlertDispatcher`가 동일 재현 | — |
| `LocalOpsDbContext.Connection/OpenAsync` + `Microsoft.Data.Sqlite 8.0.11` | Phase-2 마이그레이터 요구 충족 | `Data/LocalOpsDbContext.cs`, `Data.csproj:9` |
| 자동 알림 Telegram 출력 포맷 | 렌더러가 아이콘·`<b>`·본문까지 바이트 동일 재현 | `TelegramNotificationRenderer.cs` |
| `command_log.chat_id NOT NULL` | 확인 (non-Telegram 명령 부적합 → V2 신규 테이블 근거) | `SqliteMigrator.cs:56` |
| 기존 파이프 `Homebase.NotificationPipe` (Agent=서버, 단방향) | 확인 → 새 `Homebase.SessionBridge`는 별개 파이프 | `Agent/Services/NotificationBridgeServer.cs` |

**아직 없는 것 (신규 생성 대상)**: `src/`에는 `LocalOpsBot.Protocol` / `LocalOpsBot.Transport.*` 프로젝트가 없다. 설계서 01의 8프로젝트 구성은 목표형이며 단계적으로 추출한다.

---

## 3. 코딩 전에 반드시 잠글 사항 (Blockers)

각 TRAIN을 시작하기 전에 해결해야 하는 선결 조건이다.

- **[TRAIN-C] `protocol-lock.json` 부재 + SHA placeholder.** 설계서가 참조하는 `docs/protocol-lock.json`은 번들에 존재하지 않고, 06·22 문서의 커밋 SHA는 `<commit-sha>` placeholder다. **실제 Android 기기 capture로 SHA와 fixture를 확정하기 전에는 KDE wire 코드를 작성하지 않는다.**
- **[TRAIN-B] `session-ipc.schema.json`의 `operation` 필수 제약 vs control frame 충돌.** 스키마는 모든 `kind`에 `operation`(minLength 1)을 요구하지만, `hello/challenge/heartbeat/goodbye` 등 control frame에는 의미가 없다. 스키마를 `kind` 조건부로 고치거나 handshake frame에 placeholder operation을 넣을지 **코딩 전에 결정**한다.
- **[TRAIN-A GOAL-05] DB 마이그레이션 방식 결정.** 08 문서는 `schema_migration_history`(checksum·backup·rollback) 테이블을 제안하지만 Phase-2 킷은 기존 `schema_version`에 per-migration transaction만 적용하는 간소화 버전이다. **둘 중 무엇을 GOAL-05 범위로 삼을지 결정**한다(§6.2 권고안 참조).

---

## 4. 결정 기록 (Decisions)

| # | 결정 | 내용 | 상태 |
|---|---|---|---|
| D-1 | **알림 포워딩 포맷 신규 수용** | Toast 알림 포워딩 Telegram 메시지가 기존 `🔔 <b>앱</b>\nTitle:…\nBody:…` → 신규 `ℹ️ <b>앱: 제목</b>\n본문`으로 바뀐다. 렌더러를 그대로 두고 **CHANGELOG에 명시**한다. 추가 코드 불필요. | **확정 (사용자)** |
| D-2 | 산출물 범위 | 이번 작업은 **계획 문서까지**. 실제 코드 적용은 별도 승인 후. | **확정 (사용자)** |
| D-3 | Telegram 제거 안 함 (ADR-001) | Telegram은 외부 접근·비상 채널로 유지. | 설계 채택 |
| D-4 | KDE는 Adapter (ADR-002) | Core는 KDE packet type을 몰라야 함. | 설계 채택 |
| D-5 | 네이밍 이중성 수용 | 파이프·config는 `Homebase.*`, 프로젝트/네임스페이스는 `LocalOpsBot.*`. 코드 생성 시 혼동 주의. | 확인 |

---

## 5. 주의해야 할 내부 불일치 (구현 시 참조)

- **전이 참조 의존**: `apply-phase1.ps1`은 Core·Tests에만 Protocol 참조를 추가한다. 교체 `AlertDispatcher`는 `MessageSensitivity`(Protocol)를 직접 쓰므로 Agent는 Core 경유 전이 참조에 의존한다. .NET SDK 기본값상 컴파일되지만 **첫 빌드로 반드시 확인**. 실패 시 Agent/Infrastructure에 명시적 Protocol 참조 추가.
- **`schema_version.applied_at` 포맷 변경**: Phase-2 마이그레이터는 `datetime('now')` → ISO `"O"`. 기존 V1 DB에는 영향 없음(무해).
- **두 "delivery" 개념 혼동 주의**: envelope의 `DeliverySemantics{BestEffort,AtLeastOnce,Acknowledged}` vs 라우팅 `DeliveryPolicy{OriginOnly,LocalPreferred,…}`. 또한 envelope `MessageSensitivity{Normal,Private,Sensitive,Secret}` vs 알림 privacy mode `{Allow,HideContent,BlockImages,MetadataOnly,Block}`. 서로 다른 축이다.
- **hopCount/maxHops**: 스키마는 두 값을 독립 허용(0..8 / 1..8)하지만 Core 검증기는 `HopCount < MaxHops`를 요구. **런타임 검증이 authoritative**, 스키마만 믿지 말 것.
- **`IRemoteCommandRouter`/`LegacyRemoteCommandRouter`는 Phase-1에서 등록만 되고 배선되지 않는다**(의도적). `TelegramPollingService`는 TRAIN-A에서 건드리지 않는다. 실제 KDE ingress 배선은 TRAIN-C.

---

## 6. TRAIN-A: Foundation — 상세 실행 계획

킷 검증이 끝나 **즉시 착수 가능**. 두 개의 독립 PR로 나눈다.

### 6.1 Phase 1 — Protocol + Outbound 라우팅 (PR 1, 반나절 규모)

**신규 프로젝트**: `src/LocalOpsBot.Protocol` (BCL만 의존, `System.Text.Json`)

**신규 파일** (킷이 복사)
- `Protocol/Messaging/`: `DeviceEnvelope`, `DeviceEnvelopeJson`, `DeviceEnvelopeValidator`, `EndpointAddress`, `PayloadDescriptor`
- `Core/Commands/`: `RemoteCommand`, `IRemoteCommandRouter`, `LegacyRemoteCommandRouter`
- `Core/Delivery/`: `DeliveryPolicy`, `IOutboundChannel`, `IOutboundRouter`, `OutboundAttemptResult`, `OutboundNotification`, `OutboundRouter`
- `Infrastructure/Telegram/`: `TelegramNotificationRenderer`, `TelegramOutboundChannel`
- `tests/`: `DeviceEnvelopeValidatorTests`, `OutboundRouterTests`, `LegacyRemoteCommandRouterTests`

**실행 순서**
```powershell
# 0) baseline 고정 (GOAL-00)
git checkout main; git pull
git checkout -b feature/device-hub-foundation
dotnet build LocalOpsBot.sln -c Debug
dotnet test  LocalOpsBot.sln -c Debug          # 현재 green 확인

# 1) 신규 파일 복사 + sln/참조 자동 추가
Set-ExecutionPolicy -Scope Process Bypass
.\docs\homebase-connect-design-bundle\implementation-kit\phase-1\apply-phase1.ps1 `
  -RepoRoot (Get-Location).Path -Apply

# 2) patch-guides 수동 적용 (숫자 순서)
#   02: Core/ServiceCollectionExtensions.cs
#       → using LocalOpsBot.Core.Delivery;
#       → AddSingleton<ICommandRouter,CommandRouter>() 직후:
#         AddSingleton<IRemoteCommandRouter, LegacyRemoteCommandRouter>();
#         AddSingleton<IOutboundRouter, OutboundRouter>();
#   03: Infrastructure/ServiceCollectionExtensions.cs
#       → using LocalOpsBot.Core.Delivery;
#       → AddLocalOpsTelegram 내 HttpClient 등록 뒤:
#         AddSingleton<TelegramNotificationRenderer>();
#         AddSingleton<IOutboundChannel, TelegramOutboundChannel>();
#   04: Agent/Services/AlertDispatcher.cs           ← 교체
#   05: Agent/Services/NotificationForwardingService.cs ← 교체
#   06: (문서만, 코드 변경 없음)
#   01/07: csproj 참조 (스크립트가 이미 수행)

# 3) 검증
.\docs\homebase-connect-design-bundle\implementation-kit\phase-1\verify-phase1.ps1 `
  -RepoRoot (Get-Location).Path              # restore/build/test
```

**동작 흐름 (Phase 1 후)**
```
Monitor → AlertEvent → AlertPolicy(mute/dedup/rate) → OutboundNotification
        → OutboundRouter → TelegramOutboundChannel → Telegram
```
kdeconnect 채널이 없으므로 `Both`·`TelegramFallback`·`LocalPreferred` 모두 Telegram으로 귀결 → **자동 알림 출력 불변**.

**완료 기준 (Definition of Done)**
- [ ] 신규 테스트 통과 + 기존 테스트 회귀 없음
- [ ] `/ping`·`/status`·`/alerts`·boot alert 출력 golden 동일
- [ ] `AlertDispatcher`에서 `ITelegramClient` 직접 의존 제거 확인
- [ ] fresh config에서 Telegram 정상 동작
- [ ] **D-1** 알림 포워딩 포맷 변경을 실측 확인하고 CHANGELOG에 기록
- [ ] 첫 빌드에서 Agent/Infrastructure의 Protocol 전이 참조 정상 확인 (§5)

### 6.2 Phase 2 — Device Registry + SQLite V2 (PR 2, GOAL-05)

Phase 1 머지 후 별도 PR. `SqliteMigrator`를 킷 버전으로 교체하여 V1 보존 + V2 테이블(트랜잭션) 추가.

**V2 테이블**: `device`, `device_capability`, `device_plugin_setting`, `device_session`, `device_state`, `command_execution_log`, `message_delivery`, `outbox_message`, `payload_transfer` (모두 신규, V1 rebuild 없음).

**Blocker 결정 (§3)** — 권고: **GOAL-05는 킷 버전(per-migration transaction + rollback, `schema_version` 유지)으로 착수**하고, 08 문서의 `schema_migration_history`·checksum·pre-migration backup은 **하드닝 후속(별도 이슈)**으로 분리한다. 이유: 킷은 이미 트랜잭션·롤백을 제공해 데이터 안전의 핵심을 충족하고, checksum/history는 운영 관측성 개선이라 릴리스 게이트가 아니다.

**검증 (`MIGRATION_TEST_MATRIX.md`)** — 반드시 실 DB 백업 후:
1. Fresh DB → `schema_version` 1,2 존재, V1·V2 테이블 존재
2. 기존 V1 DB → `runtime_state`·`command_log`·`alert_log`·`notification_event` row count 불변, V2 추가
3. 실패 주입(V2 중간 invalid SQL) → 롤백, version 2 미삽입, Agent 시작 가시적 실패, V1 데이터 보존
4. 반복 실행 → 중복 version 없음, 예외 없음, 데이터 손실 없음

**완료 기준**: 위 4개 시나리오 통과 + device CRUD/revoke 테스트 + 롤백 문서화.

---

## 7. TRAIN-B: Local Session / Agent–Tray 양방향 IPC (GOAL-06)

기존 단방향 알림 파이프를 유지한 채, **양방향 request/response/event bridge**를 shadow mode로 추가한다. Android 알림 action·clipboard·URL/file open·media·사용자 확인에 필요.

**핵심 계약**
- 파이프 `\\.\pipe\Homebase.SessionBridge`, **Agent=서버 / Tray=클라이언트**, `InOut`, 최대 8 인스턴스. 연결 후 Windows Session ID 등록(멀티유저).
- Frame: 4바이트 LE 길이 접두 + UTF-8 JSON, `ReadExactlyAsync`, control frame ≤ 1 MiB. **바이너리는 base64-in-JSON 금지** — temp-file descriptor 또는 별도 stream 채널.
- envelope `kind ∈ {hello, challenge, challenge_response, request, response, event, cancel, heartbeat, goodbye}` (`schemas/session-ipc.schema.json`).
- Handshake: Tray hello(pid/sessionId/userSid/nonce) → Agent가 **pipe impersonation**으로 실제 client identity 확인 → SID/session 검증 → challenge → Tray가 **DPAPI 보호 per-install IPC secret**으로 HMAC 서명 → capability 협상 → Active.
- 연산: Tray→Agent 이벤트(`toast.posted/updated/removed`, `clipboard.changed`, `media.session.changed`, `tray.ready/health`, `user.confirmation.result`); Agent→Tray 요청(`toast.dismiss/invokeAction`, `clipboard.get/set`, `url.open`, `file.open`, `media.*`, `user.confirm`).
- 타임아웃(대개 10s, user.confirm 60s), 재연결 backoff 1/2/5/10/30s, heartbeat 15s·3회 miss 시 disconnect. Disconnect 시 pending은 **`UserSessionUnavailable`** typed 실패.

**의존성**: TRAIN-A DI/hosting 위에. **typed `UserSessionUnavailable`(R-12)는 TRAIN-D의 모든 user-session 기능이 소비하는 선결 계약** → B가 그 D 기능들보다 먼저 land해야 함. C의 인터랙티브 페어링 UI도 B의 `user.confirm`/event에 의존.

**보안 (리뷰 필수)**
- **R-04 (Critical)**: Agent가 LocalSystem이면 저권한 사용자가 SYSTEM 파이프에 접속하는 권한 상승 위협. 파이프 ACL은 **LocalSystem + Built-in Administrators + 설치 사용자/활성 세션 SID만** 허용, **Authenticated Users·Everyone·Anonymous 금지**. + operation allowlist. **중지 조건: client identity를 신뢰성 있게 검증 못하면 GOAL 중단.**
- DPAPI per-install secret, challenge/response 인증.
- 보안 로깅 allow/deny: 연결 accept/reject·SID mismatch·op명/duration은 기록, clipboard·알림 본문·파일 내용·challenge secret·token은 **절대 미기록**.

**코딩 전 lock (§3)**: `session-ipc.schema.json`의 `operation` 조건부화 결정.

**Migration**: 기존 파이프 유지 → bridge를 feature flag 뒤 추가 → **toast를 두 파이프에 shadow-send** → 비교 → bridge 기본화 → 구파이프 제거.

**외부 검증 필요**: pipe impersonation·SID 검증은 Windows 런타임 특화 → 실제 .NET SDK 빌드 + on-Windows 통합 테스트 필수.

---

## 8. TRAIN-C: KDE Connect 호환 (GOAL 07–10)

**GPL/MIT 경계**: KDE Java/Kotlin 코드를 복사·이식하지 않고 **외부 동작 기준 독립 재구현**(R-03). 신규 격리 프로젝트 `src/LocalOpsBot.Transport.KdeConnect`. Core는 KDE packet 이름을 절대 몰라야 함.

### GOAL-07: Protocol Conformance Spike (**나머지 전부의 게이트**)
- `docs/protocol-lock.json`에 upstream/fork **정확한 commit SHA를 실제 기기 capture로 확정** (현재 placeholder).
- identity/pair/ping + notification/share/run-command **fixture 생성**(개인정보 제거), 최소 console peer prototype.
- 완료: 실제 Android가 prototype 발견 → pair/unpair → ping 왕복 → reconnect, fixture에 PII 없음, production 진행 검토 통과.

### GOAL-08: KDE Transport Foundation
- 컴포넌트: Discovery(UDP broadcast) · Identity codec · Certificate store/TLS · Pairing coordinator · Session registry · Send queue · Capability mapper.
- 포트 범위 `1714-1764`(UserBase 기준, **capture로 확정 필요**). Agent hosted service로 Telegram과 동시 실행. malformed packet은 해당 session만 종료.

### GOAL-09: Devices & Pairing UI · GOAL-10: Battery/Status
- Dashboard: discovered/trusted/connected, verification code, accept/reject, unpair/revoke, capability·plugin toggle. **UAC 없는 user-level 페어링 UI**(B 브리지로 pair decision 전달).
- BatteryPlugin → `device_state` → `/devices`·`/device`·Dashboard 카드.

**리스크**
- **R-14 (Critical)**: 인증서 변경 device 자동 신뢰 → fingerprint mismatch는 재페어링 강제, `rejectCertificateChanges` 기본 true.
- **R-01/R-02 (High)**: KDE wire·"Send Notifications" 호환 drift.
- **중지 조건**: identity/pair fixture를 실제 기기에서 재현 불가 / 호환에 GPL 코드 복사가 불가피 / payload가 전체 in-memory buffering으로만 가능.

**보안**: cert store PFX + machine ACL(개인키 export 금지), verification code는 Tray UI 또는 Telegram admin 채널에만, headless 페어링은 opt-in. Run-command는 **`command.predefined.invoke` capability만**(raw shell 절대 금지). 방화벽 규칙은 KDE enable 시에만·**Private 프로파일만**·실행 파일 scoped·uninstall 시 제거(포트포워딩/UPnP 없음).

**외부 검증 필요 (가장 무거움)**: 모든 fixture 작성과 production 게이트("real-device roundtrip")에 **실제 Android 기기 + packet capture** 필수. 코드 리뷰만으로 불가.

---

## 9. TRAIN-D: User Features (GOAL 11–18)

B(세션 브리지)와 C(디바이스/페어링/전송) 위에 얹는 사용자 기능. 디바이스 카드·설정·전송 UI는 C 없이는 의미 없음.

**주요 계약**
- Dashboard 카드 순서: STATUS · TELEGRAM · DEVICES · NOTIFICATIONS · TRANSFERS · MONITORS · UPDATES. Devices empty-state + "Enable device connection" CTA.
- 디바이스별 설정(→ `device_plugin_setting.settings_json`): 알림 수신/발신·Show as Toast·**Store content OFF 기본**·Share 수신+"저장 전 확인"·명령별 allow toggle(Lock PC/승인된 서비스 재시작)·Clipboard OFF·Media OFF.
- 페어링 다이얼로그: 이름/타입·단축 ID·fingerprint·verification code·만료 countdown·cert-changed 경고. **백그라운드 결과는 WPF Dispatcher로 마샬링.**
- 전송 행: progress + Cancel, **실행 파일 자동 열기 금지**.
- 데이터: `device_state`(빠른 대시보드 읽기), `payload_transfer`(+retention), `notification_metadata`/`notification_content` 분리.

**리스크**
- **R-13 (High)**: 민감 알림 저장 — privacy mode를 **저장 전에** 적용. 기본 차단: 비밀번호 관리자·authenticator·Windows Security·뱅킹·Homebase/KDE/Telegram 자기 출처.
- **R-18 (High)**: 기존 Telegram UX 회귀 — 모든 GOAL PR이 golden test로 "Telegram 동작 불변" 유지.
- **R-12 (Med/High)**: Tray 부재 시 typed unavailable 상태 표시(무음 실패 금지).
- 접근성: 키보드 내비, 선택 가능한 페어링 코드, non-color 상태 텍스트, DPI 100/125/150%.

**파일 전송 보안**: size cap·확장자 정책·reserved-name/path-traversal 검증·temp ACL·실행 파일 격리·auto-open OFF·free-disk 임계.

---

## 10. 교차 의존성 & 실행 순서

```
TRAIN-A (00–05)  ──┬─> TRAIN-B (06)  ──┬─> TRAIN-D user-session 기능 (clipboard/media/file/confirm)
                   │                   │
                   └─> TRAIN-C (07–10) ─┴─> TRAIN-D device 기능 (알림/공유/전송/배터리 UI)
                        ▲
                        └ protocol-lock.json SHA 확정 + fixture capture 이후에만 wire 코딩
```

1. **TRAIN-A** — DI 리팩터, `ITelegramClient` 분리, V1→V2 DB.
2. **TRAIN-B** — user-session 연산 + 인터랙티브 페어링 UI를 unblock.
3. **TRAIN-C** — 단, `protocol-lock.json` SHA 확정 + 실기기 fixture 이후. 알림 세부는 GOAL-07 이후.
4. **TRAIN-D** — B(세션 연산)·C(디바이스/전송) + 08 알림 프라이버시 분리 위에.

---

## 11. 리스크 레지스터 요약 (Critical/High)

| ID | 심각도 | 요지 | 완화 | TRAIN |
|---|---|---|---|---|
| R-04 | Critical | SYSTEM Agent IPC 권한 상승 | SID 검증·좁은 ACL·op allowlist | B |
| R-14 | Critical | 인증서 변경 device 자동 신뢰 | fingerprint mismatch 재페어링 강제 | C |
| R-01/02 | High | KDE wire/알림 호환 drift | commit lock + fixture 테스트 | C |
| R-13 | High | 민감 알림 저장 | 저장 전 privacy mode 적용, 차단 목록 | D |
| R-18 | High | 기존 Telegram UX 회귀 | golden test, TRAIN-A 불변 원칙 | 전체 |
| R-12 | Med/High | Tray 부재 시 동작 | typed `UserSessionUnavailable` | B→D |
| R-03 | 법적 | GPL/MIT 혼합 | 독립 재구현, 코드 미복사 | C |

---

## 12. 검증 전략 (환경별)

이 아티팩트 환경에는 **.NET SDK가 없어** 킷의 C#은 컴파일 검증되지 않았다. 아래는 실제 Homebase 환경에서 수행한다.

- **TRAIN-A**: `dotnet restore/build/test` + Telegram golden output + fresh install smoke + V2 마이그레이션 4-시나리오(실 DB 백업 후).
- **TRAIN-B**: on-Windows 통합 테스트(pipe impersonation·SID·partial header read·malformed frame 격리·재연결·shadow 비교).
- **TRAIN-C**: 실제 Android 기기 pair/ping/reconnect + 양방향 알림 + fixture roundtrip. **코드 리뷰만으로 완료 불가.**
- **TRAIN-D**: 실 페어링 기기로 end-to-end + WPF Dispatcher/DPI 수동 검증.

---

## 13. 미결정 사항 (사용자 결정 대기)

1. **DB 마이그레이션 범위** (§3, §6.2): GOAL-05를 킷 간소화 버전으로 착수 + 08의 history/checksum/backup은 후속 이슈로 분리 — 권고안대로 진행할지.
2. **session-ipc `operation` 스키마** (§3): TRAIN-B 착수 시 `kind` 조건부화 방식.
3. **멀티유저 Windows 세션 지원 시점** (R-11): 최소 버전은 설치 시 단일 사용자 SID 고정. 확장 시점 결정.
4. **KDE 장기 방향**: 완전 KDE 호환 지속 vs Homebase-native 프로토콜/Android 앱 전환 시점(ADR-006).
5. **다음 액션**: 이 계획대로 TRAIN-A Phase-1 코드 착수 승인 여부.
