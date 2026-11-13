# Decisions

되돌리기 어려운 결정(보안·데이터 형식·공개 API·인프라·주요 의존성)만 적는다. 기존 Device Hub 결정(D-1~D-5, ADR-001~006)은
[`docs/DEVICE_HUB_IMPLEMENTATION_PLAN.md`](docs/DEVICE_HUB_IMPLEMENTATION_PLAN.md)와 설계 번들에 있다.

## M — Homebase Mobile (다중 PC 실시간 모니터링 앱), 2026-11-13

요구: 휴대폰에서 여러 PC(집·회사·노트북)의 열린 창과 창(프로세스)별 CPU·RAM·디스크·네트워크를 실시간으로 보고,
Chrome·Claude 같은 앱의 활성 탭·세션 제목을 확인한다. 목적은 PC에서 에이전트로 돌리는 자동화 작업 감시.
QR로 페어링하고 GitHub Releases로 배포하며 앱 안에서 사이드로드 업데이트한다. 사용자 추가 요청: **Windows에 부담 없이 최대한 가볍게**,
**Tailscale 고정 IP로 서버리스 운영**.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-1 | **통신 경로: Tailscale 직결, 서버리스** | 휴대폰이 PC의 Tailscale 주소(MagicDNS 이름 → 고정 100.x IP 순)로 직접 붙는다. Agent는 tailnet 주소(100.64.0.0/10)에만 바인딩한다. 방화벽 규칙은 원격 100.64.0.0/10, Agent exe, TCP 47820으로 한정한다. | 중계 서버(oracle-instance 등)는 운영·인증·종단 암호화·장애 대응이 새로 생겨 기각. LAN 전용은 외부에서 못 봐 기각. **대가**: README 원칙이 "outbound만"에서 "인터넷 inbound 없음, tailnet 안에서만 inbound"로 바뀐다. 휴대폰 Tailscale이 켜져 있어야 한다. |
| M-2 | **인증: QR 일회용 코드 → 장기 토큰** | QR에는 `{hb, pcId, pcName, hosts[], port, code, expiresAt}`가 들어간다. code는 128bit, 5분, 1회용이고 새 QR이 이전 것을 무효화한다. 5분에 10회 틀리면 잠긴다. `POST /api/v1/pair`가 256bit 토큰을 발급하고 PC는 SHA-256만 저장한다(고정시간 비교). 앱은 Android Keystore AES-GCM으로 토큰을 암호화한다. 철회는 Tray "Connect Phone…" 창에서 한다. | 별도 TLS는 두지 않는다(WireGuard가 종단 암호화). 그래서 앱은 cleartext를 허용하고, 업데이트 통신만 코드에서 https를 강제한다. |
| M-3 | **모바일 스택: Kotlin + Jetpack Compose, minSdk 26** | Markleaf 업데이터를 그대로 이식할 수 있고 기존 Android 프로젝트들과 같다. 의존성은 OkHttp, zxing-android-embedded, zxing core뿐이다(JSON은 플랫폼 org.json). release APK 약 1.4MB. | Flutter는 iOS 가능성이 장점이지만 iOS 사이드로드 배포가 불가능해 이점이 없다. |
| M-4 | **같은 저장소 `mobile/`, 버전 통합** | `v*` 태그 하나로 Windows 설치본과 `Homebase-Mobile.apk`, `update.json`을 함께 릴리스한다. versionCode = major·10000 + minor·100 + patch. 업데이터는 사이드로드 전용이라 Markleaf의 스토어 게이트·스텁 없이 항상 포함되고 기본 켜짐이다. | 별도 저장소는 프로토콜 변경을 두 저장소에 맞춰야 한다. **대가**: PC만 바뀐 릴리스에도 앱 업데이트 배너가 뜬다. |
| M-5 | **프로토콜 v1** | `GET /api/v1/info`(공개: 이름·버전), `POST /api/v1/pair`, `GET /api/v1/snapshot`(Bearer), `WS /api/v1/live`(Bearer, 1초 스냅샷)를 둔다. 모든 메시지에 `schema: 1`을 넣고, 앱은 다른 schema를 반쪽 파싱하지 않고 "버전 불일치"로 멈춘다. 형식은 `docs/mobile-protocol/*.json` 픽스처를 .NET과 Kotlin 테스트가 함께 검증한다. | — |
| M-6 | **서버: 소켓 + 최소 HTTP/1.1 + `WebSocket.CreateFromStream`** | 의존성 0. tailnet IP 소켓에만 바인딩한다. | **Kestrel**은 상주 서비스에 ASP.NET 런타임(설치본 +15~20MB, 메모리)을 싣는다. **HttpListener(http.sys)**는 특정 IP 프리픽스여도 포트를 모든 인터페이스에 열고 Host 헤더로 라우팅한다. 게다가 트래픽 소유자가 System 프로세스라 exe 범위 방화벽 규칙으로 막을 수 없다. 둘 다 기각. |
| M-7 | **프로세스별 네트워크: ETW `Microsoft-Windows-Kernel-Network` 직접 P/Invoke** | 전용 실시간 세션을 쓴다(NT Kernel Logger가 아님). 이벤트 페이로드의 PID와 size 오프셋만 읽어 패킷마다 할당이 없다. 관리자(LocalSystem)가 필요하고, 실패하면 "사용 불가"로 표시한다. | TraceEvent(MIT)는 어셈블리와 매니페스트 파싱 비용 때문에 기각. EStats는 연결별로 켜야 해서 기각. |
| M-8 | **디스크는 "I/O"로 표기** | `GetProcessIoCounters`의 read+write 바이트 증분을 쓴다. 파일·일부 장치 I/O가 섞인 근사치다. | ETW Kernel-Disk는 비용 대비 이득이 작다. |
| M-9 | **Claude 세션 제목: Claude Code 트랜스크립트 끝부분만** | Tray가 `%USERPROFILE%\.claude\projects\*\*.jsonl` 중 10분 안에 바뀐 파일의 끝 64KB만 읽는다. 가져오는 것은 `customTitle`→`aiTitle`, `cwd`, 수정 시각뿐이다. 프롬프트·응답 본문은 결과에 넣지 않는다. 파일별 mtime 캐시를 둔다. `mobile.claudeSessions`로 끈다. | Claude 데스크톱 창 제목은 `Claude`뿐이라 창 제목만으로는 요구를 못 채운다. |
| M-10 | **Tray↔Agent: 새 duplex 파이프 `Homebase.MobileBridge`** | 4B LE 길이 + JSON, 버퍼 64KB를 쓴다. ACL은 서버 자신(인스턴스 생성), SYSTEM, Administrators, `mobile.userSid`(없으면 INTERACTIVE)이고 AuthenticatedUsers는 넣지 않는다. Tray가 보낼 수 있는 op는 `session.report`, `pairing.begin`, `devices.list`, `devices.revoke`, `status`로 제한하며 그 밖은 연결을 끊는다. | Device Hub TRAIN-B SessionBridge의 좁은 선행 조각이다. B가 착수되면 흡수한다. |
| M-11 | **기기 저장소: `C:\ProgramData\Homebase\mobile\state.json`** | ACL은 SYSTEM과 Administrators만이다. 원자적으로 쓴다. 파일이 손상되면 Agent(Telegram 감시)를 멈추지 않고, 원본을 `state.json.corrupt-*`로 보존한 뒤 오류 로그를 남기고 빈 상태로 시작한다. 이때 휴대폰은 401을 받고 다시 페어링하라는 안내를 본다. | SQLite V2 `device` 테이블(GOAL-05)이 아직 미결이라 스키마 번호 충돌을 피했다. GOAL-05가 끝나면 그 테이블로 옮긴다. |
| M-12 | **릴리스 서명 키** | `homebase-release.jks`(PKCS12, RSA 4096, alias `homebase`)를 쓴다. 인증서 SHA-256은 `4d9cbd4d68cd2ee792fd1f506cbfae9c2313186b668a5ca34d9020f5d23ef865`이다. 로컬 원본은 `D:\Project\homebase\.keystore\`(git 제외, `.git/info/exclude`)에 있고 `G:\내 드라이브\모바일 앱 개발\Homebase\릴리즈 키스토어\`에 백업했다(cmp 일치). CI 시크릿은 `HOMEBASE_RELEASE_KEYSTORE_BASE64`, `_STORE_PASSWORD`, `_KEY_ALIAS`, `_KEY_PASSWORD`이며, 릴리스 워크플로가 인증서 지문 불일치 시 실패한다. | 키를 잃으면 같은 앱으로 업데이트를 낼 수 없다. |
| M-13 | **"보고 있을 때만 수집"** | 첫 구독자가 생기면 샘플러, ETW 세션, Tray의 창·세션 보고가 시작되고 마지막 구독자가 떠나면 모두 멈춘다. 앱은 화면이 STARTED일 때만 WebSocket을 연다. 측정값(프로세스 약 300개, 이 PC): 비감시 시 한 코어의 0.05%, 감시 중 Agent 약 3.2%와 Tray 약 +0.8%p. | — |
| M-14 | **방화벽 규칙은 Agent가 관리** | `mobile.enabled`에 맞춰 Agent(LocalSystem)가 시작 시 규칙을 추가·삭제한다. 그래서 규칙이 항상 실행 중인 exe 경로를 가리킨다. 언인스톨 스크립트도 규칙을 지운다. | 계획의 `installer/enable-mobile.ps1`은 만들지 않았다. Tray "Connect Phone…"의 ENABLE 한 번(UAC)으로 설정과 재시작이 끝나 별도 스크립트가 필요 없다. |

### Device Hub 계획과의 관계

- **ADR-006 개정**: "stock KDE Connect 앱 우선, 전용 앱은 검증 후" → **모니터링 뷰어는 전용 Homebase 앱, 기기 연동(알림 미러링·공유·배터리·명령)은 계속 KDE Connect(TRAIN C/D)**.
  미결 #4(KDE 장기 방향) 중 "Homebase-native 프로토콜/앱"의 범위가 모니터링으로 정해졌다.
- 페어링 레지스트리 이중화(KDE TLS 페어링 ↔ QR 토큰)는 GOAL-05 `device` 테이블에서 `transport` 구분으로 통합한다(후속).
- 원격 명령은 이 앱 범위가 아니다(읽기 전용 모니터링). ADR-005(raw shell 금지)와 충돌 없음.
