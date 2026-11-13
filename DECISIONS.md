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
| M-1 | **통신 경로: Tailscale 직결, 서버리스** (M-15~M-18로 확장 — Tailscale은 이제 선택) | 휴대폰이 PC의 Tailscale 주소(MagicDNS 이름 → 고정 100.x IP 순)로 직접 붙는다. Agent는 tailnet 주소(100.64.0.0/10)에만 바인딩한다. 방화벽 규칙은 원격 100.64.0.0/10, Agent exe, TCP 47820으로 한정한다. | 중계 서버(oracle-instance 등)는 운영·인증·종단 암호화·장애 대응이 새로 생겨 기각. LAN 전용은 외부에서 못 봐 기각. **대가**: README 원칙이 "outbound만"에서 "인터넷 inbound 없음, tailnet 안에서만 inbound"로 바뀐다. 휴대폰 Tailscale이 켜져 있어야 한다. |
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

## M (계속) — Tailscale 선택화: LAN·Cloudflare Tunnel 경로, 2026-11-13

요구: "Tailscale 없이도 되게, Tailscale은 옵션". 같은 LAN 직접 연결과 Cloudflare Tunnel 외부 연결을 더한다.
사용자 추가 요청: **QR로 연결하면 경로 선택이 자동**(Tailscale 대역이면 Tailscale, 같은 Wi-Fi면 LAN, 아니면 Cloudflare), **빠진 구성 요소는 설정에서 설치**.
Cloudflare 방식(이름 있는 터널 우선 + Quick Tunnel, Agent 하위 프로세스, Bearer만)은 비교안을 보고 사용자가 골랐다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-15 | **LAN 경로: 사설 IP 바인딩 + TLS 핀닝** | `mobile.lan`이 켜지면 Agent가 기본 게이트웨이가 있는 어댑터의 RFC 1918 주소에 바인딩한다(Hyper-V·WSL·VMware 가상 스위치 제외). 그 리스너만 `SslStream`(TLS 1.2/1.3)이다. 인증서는 자체 서명 ECDSA P-256(30년)이며 Agent 계정의 개인 저장소(서비스에서는 SYSTEM 저장소)에 키를 영속화해 둔다. QR에 DER의 SHA-256을 넣고, 앱은 그 인증서만 신뢰한다(CA·호스트명·만료 검사 대신 핀). 방화벽 규칙 `Homebase Mobile (LAN)`은 원격 LocalSubnet, Private 프로필, Agent exe, TCP 포트로 한정한다. 소켓 단에서도 같은 서브넷의 사설 주소만 받는다. | 평문 LAN은 같은 Wi-Fi의 누구나 토큰을 엿볼 수 있어 기각. 공인 인증서는 사설 IP에 발급할 수 없다. **대가**: 인증서를 바꾸면(저장소 삭제 등) 휴대폰을 다시 페어링해야 한다. Windows가 그 Wi-Fi를 '공용'으로 보면 LAN이 막힌다(트레이가 감지해 안내). |
| M-16 | **QR·경로 형식: `endpoints[]` 추가, 스키마 유지** | 각 경로를 `{transport, host, port, tls, certSha256?}`로 나타낸다. `hosts`+`port`는 이전 앱용 Tailscale 목록으로 남겨 `hb`/`schema`를 올리지 않는다. 인증된 `GET /api/v1/endpoints`가 현재 경로를 돌려준다. 앱은 재연결할 때마다 이를 받아 DHCP 변경이나 Quick Tunnel 새 주소를 따라간다. | `hosts`를 객체 배열로 바꾸고 `hb: 2`로 올리는 안은 기존 앱에서 QR 자체를 못 읽게 만들어 기각. |
| M-17 | **Cloudflare Tunnel: cloudflared를 Agent 하위 프로세스로, 토큰 있으면 이름 있는 터널·없으면 Quick Tunnel, 인증은 Bearer만** | `mobile.cloudflare.enabled`이면 Agent가 cloudflared를 실행한다. 대상은 `mobile.cloudflare.cloudflaredPath`, winget/MSI 설치 폴더, PATH 순으로 찾는다. 토큰이 있으면 `tunnel run`으로 띄우고 토큰은 `TUNNEL_TOKEN` 환경 변수로 넘긴다(명령줄 노출 없음). 이때 `hostname`이 필수다. 토큰이 없으면 `tunnel --url http://127.0.0.1:<port>`로 띄우고, 로그에서 `*.trycloudflare.com` 주소를 읽는다. 연결 대상(origin)은 루프백 평문 리스너다. 프로세스는 kill-on-close Job Object에 넣어 Agent와 함께 죽는다. 실패하면 백오프(5초→15초→1분→5분)로 재시작하고, 설치가 안 되어 있으면 1분마다 다시 찾는다. 인바운드 방화벽 규칙은 필요 없다. | **Windows 서비스(`cloudflared service install`)**: Quick Tunnel 주소를 Agent가 알 수 없고 이름 있는 터널만 된다. **Cloudflare Access 서비스 토큰**: 노출 면은 줄지만 QR·저장·갱신 항목이 늘어 이번엔 제외(Bearer 256bit + 페어링 코드 잠금 유지). **cloudflared 번들**: 설치본이 수십 MB 커진다 → winget 설치 버튼. **대가**: 켜 두면 cloudflared가 상시 실행된다(유휴 시 CPU 거의 0, 메모리 수십 MB) — "보지 않을 때 수집 0"은 유지되지만 연결 대기 프로세스가 하나 는다. Quick Tunnel은 Cloudflare가 테스트용·가용성 무보장으로 명시하고, PC 재시작마다 주소가 바뀌어 밖에서는 앱이 LAN/Tailscale로 한 번 붙기 전까지 새 주소를 모른다. `/api/v1/info`(PC 이름·버전·OS)와 페어링 엔드포인트가 터널 주소를 아는 누구에게나 열린다. 터널 토큰은 Telegram 토큰과 같은 `ProgramData` 설정 파일에 두며 `ENV:` 간접 참조를 지원한다. |
| M-18 | **앱 경로 선택: 네트워크 힌트로 순위, 전부 시도** | 앱이 `ConnectivityManager`로 휴대폰의 비VPN IPv4 서브넷과 Tailscale VPN(100.64/10 주소) 여부를 읽는다. 순위는 같은 서브넷 LAN → (VPN 켜짐) Tailscale → Cloudflare → 나머지(VPN 꺼진 Tailscale, 다른 서브넷 LAN, loopback)다. 같은 순위 안에서는 마지막 성공 경로를 먼저 쓴다. 감지는 힌트일 뿐 거르지 않으며, 실패하면 다음 경로로 넘어간다. 상세·목록 화면에 붙은 경로를 표시한다. `ACCESS_NETWORK_STATE` 권한을 추가했다. | "마지막 성공 경로 우선"만 쓰면 집 밖에서 LAN 타임아웃을 매번 기다린다. 경로를 병렬로 경주시키는 안은 페어링(1회용 코드)·WebSocket에 부작용이 있어 기각. |

## M (계속) — 알림 채널: Telegram → 앱 푸시 검토, 2026-11-13 (제안, 미확정)

요구(검토): "Telegram 봇 알림을 없애고 앱 푸시 알림으로 가면 어때?" 인벤토리·4안 비교표·근거는 [`docs/NOTIFICATION_CHANNEL.md`](docs/NOTIFICATION_CHANNEL.md).

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-19 | **제안: Telegram 유지 + 앱 푸시는 UnifiedPush(RFC 8291 암호화)로 선택 채널 추가** — 사용자 확인(실제로 쓰는 알림, Telegram 명령 사용 여부) 전까지 미확정 | Telegram은 알림 채널이면서 **유일한 명령 채널**이다(명령 20개, 그중 `/mute`·`/unmute`·`/update`는 상태 변경). 앱 푸시가 필요하면 새 `IOutboundChannel`로 UnifiedPush를 더한다. 앱이 배포자 엔드포인트를 인증 API로 각 PC에 등록하고, PC는 RFC 8291로 암호화해 POST한다. 중계 서버는 암호문만 본다(지금의 Telegram 평문보다 낫다). 의존성은 양쪽 모두 0이다(플랫폼 암호). `BootNotificationService`·Tray 테스트 메시지는 router를 거치도록 바꾼다. 완전 대체는 앱에 알림 이력 화면과 최소 명령이 생기고 "앱 읽기 전용" 원칙을 따로 개정한 뒤 다시 판단한다. | **FCM**: 공개 사이드로드 APK가 한 Firebase 프로젝트에 묶이므로 서비스 계정 키를 배포하거나 중계 서버를 둬야 한다(M-1). Firebase·Play 서비스 의존성도 생긴다(M-3). **앱 포그라운드 서비스 + PC별 상주 연결**: 상시 알림, PC 수에 비례하는 배터리 비용, OEM 종료, `dataSync` 24시간당 6시간 한도, Quick Tunnel 주소 변경, M-13 분리 작업. **ntfy 자체 호스팅**: VPS(M-1) 또는 tailnet 내부(Tailscale 필요). **대가**: 휴대폰에 배포자 앱(ntfy)을 설치해야 하고, ntfy.sh라는 제3자 서버에 메타데이터(시각·크기)가 남는다. |

### Device Hub 계획과의 관계

- **ADR-006 개정**: "stock KDE Connect 앱 우선, 전용 앱은 검증 후" → **모니터링 뷰어는 전용 Homebase 앱, 기기 연동(알림 미러링·공유·배터리·명령)은 계속 KDE Connect(TRAIN C/D)**.
  미결 #4(KDE 장기 방향) 중 "Homebase-native 프로토콜/앱"의 범위가 모니터링으로 정해졌다.
- 페어링 레지스트리 이중화(KDE TLS 페어링 ↔ QR 토큰)는 GOAL-05 `device` 테이블에서 `transport` 구분으로 통합한다(후속).
- 원격 명령은 이 앱 범위가 아니다(읽기 전용 모니터링). ADR-005(raw shell 금지)와 충돌 없음.
