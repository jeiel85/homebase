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

## M (계속) — 알림 채널: Telegram → 앱 푸시, 2026-11-13 (확정 — 1단계 구현)

요구(검토): "Telegram 봇 알림을 없애고 앱 푸시 알림으로 가면 어때?" 인벤토리·4안 비교표·근거는 [`docs/NOTIFICATION_CHANNEL.md`](docs/NOTIFICATION_CHANNEL.md).

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-19 | **확정(1단계): UnifiedPush(RFC 8291 암호화)를 주 알림 채널로, Telegram은 전환 기간 대체 채널을 거쳐 제거** — 사용자 답변 반영(명령은 거의 안 씀, Windows 알림은 웬만하면 다 받기). 1단계(UnifiedPush 추가, Telegram은 대체 채널로 유지)는 구현했다. 2단계(Telegram 제거)는 실사용 확인 뒤([`BACKLOG.md`](BACKLOG.md)) | Telegram은 지금 유일한 명령 채널이다(명령 20개). 하지만 실험 기능이라 거의 쓰지 않는다. 앞으로 주로 흐를 내용은 메신저·OTP가 섞인 Windows 토스트이므로, Telegram 평문 클라우드를 대신할 암호화 채널이 필요하다. 새 `IOutboundChannel`로 UnifiedPush를 더한다. 앱이 배포자 엔드포인트를 인증 API로 각 PC에 등록하고, PC는 RFC 8291로 암호화해 POST한다. 중계 서버는 암호문만 본다. 의존성은 양쪽 모두 0이다(플랫폼 암호). 전환 기간에는 UnifiedPush를 우선하고 실패할 때만 Telegram으로 보낸다. 실사용에서 놓치는 알림이 없다고 확인되면 Telegram 채널·폴링·명령을 제거하고, 설치 마법사의 봇 토큰 필수 입력도 없앤다. `/mute`는 Tray 로컬 메뉴로 옮긴다(앱은 읽기 전용 유지). 토스트는 기본 켜짐·BlockList로 하고, 차단 목록·마스킹은 유지한다. 토스트에는 앱별 묶음·속도 제한을 새로 둔다. `BootNotificationService`·Tray 테스트 메시지는 router를 거치도록 바꾼다. | **FCM**: 공개 사이드로드 APK가 한 Firebase 프로젝트에 묶이므로 서비스 계정 키를 배포하거나 중계 서버를 둬야 한다(M-1). Firebase·Play 서비스 의존성도 생긴다(M-3). **앱 포그라운드 서비스 + PC별 상주 연결**: 상시 알림, Doze 중 연결을 유지하려면 사용자가 직접 켜야 하는 배터리 최적화 예외(포그라운드 서비스는 App Standby만 막음), PC 수에 비례하는 배터리 비용, OEM 종료, `dataSync` 24시간당 6시간 한도, Quick Tunnel 주소 변경, M-13 분리 작업. **ntfy 평문 게시**: 토스트 내용이 제3자 서버에 평문으로 남는다. **ntfy 자체 호스팅**: VPS(M-1) 또는 tailnet 내부(Tailscale 필요). **대가**: 휴대폰에 배포자 앱(ntfy)을 설치해야 하고, ntfy.sh라는 제3자 서버에 메타데이터(시각·크기)가 남으며, ntfy.sh 익명 버스트·일일 한도에 걸릴 수 있다(착수 전 `/v1/account`로 확인). |

## M (계속) — 페어링 QR을 https 링크로, 2026-11-13

요구(사용자): "PC에서 QR을 읽었을 때 앱이 설치되지 않은 상태라면 설치 안내 페이지로 이동". 지금 QR은 JSON 원문이라 휴대폰 기본 카메라로 찍으면 글자만 보인다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-20 | **QR = `https://jeiel85.github.io/homebase/pair/#<base64url(offer JSON)>`** | 트레이가 QR에 JSON 대신 이 링크를 넣는다(`PairingLink.Build`). JSON은 `{hb, pcId, pcName, code, expiresAt, endpoints}`이고, 링크를 읽는 앱은 모두 `endpoints`를 알므로 v1 `hosts`/`port`는 뺐다. 오퍼는 fragment(`#`)에 있어 브라우저가 서버로 보내지 않는다. 설치 안내 페이지는 외부 리소스를 쓰지 않고 `no-referrer`다. 앱은 `https://jeiel85.github.io/homebase/pair*`(autoVerify App Link)와 `homebase://pair/<payload>`를 받는다. 링크로 열리면 페어링 화면에 채워만 두고, 사용자가 **연결**을 눌러야 페어링한다(웹 페이지가 임의 링크를 보낼 수 있으므로). 앱이 없으면 카메라가 `docs/pair/` 페이지를 연다. 이 페이지는 PC 이름·만료까지 남은 시간을 로컬에서 디코드해 보여 주고, APK 다운로드·'이 출처 허용' 안내·**앱에서 열기**(`intent://pair/<payload>#Intent;scheme=homebase;package=…;S.browser_fallback_url=…`)를 제공한다. 앱의 스캐너·붙여넣기는 링크와 이전 JSON QR을 모두 읽는다. 화면 표시용이라 ECC를 M에서 L로 낮췄다. 링크 약 750자에서 QR 버전 19(101모듈)로, 이전 JSON QR(M, 버전 20~23)보다 작거나 같다. 형식은 `docs/mobile-protocol/pairing-link.v1.txt` 픽스처를 .NET과 Kotlin 테스트가 함께 검증한다. | **커스텀 스킴만(`homebase://…`)**: 카메라 앱이 대부분 열지 않거나, 앱이 없으면 아무 일도 일어나지 않아 요구를 못 채운다. **쿼리스트링(`?d=`)**: 1회용 코드가 GitHub Pages 접근 로그에 남는다. **압축(deflate)**: QR은 더 작아지지만 페이지가 PC 이름·만료를 보여 주기 어렵고, 크기도 이미 이전 수준이다. **대가**: v0.14.0 앱은 새 QR을 못 읽는다("페어링 QR이 아님"). 첫 릴리스 직후이고 사용자 휴대폰에 설치 이력이 없어 감수했다. 링크가 `jeiel85.github.io`에 묶인다. App Link 자동 검증에는 그 사용자 사이트 저장소(`jeiel85/jeiel85.github.io`) 루트의 `/.well-known/assetlinks.json`(패키지 `io.github.jeiel85.homebase`, 릴리스 인증서 SHA-256 M-12)이 필요하다 — 2026-11-13 사용자 승인으로 추가(e3a10d4). 릴리스 키를 바꾸면 이 파일도 바꿔야 하고, 그 저장소의 Pages 워크플로는 `.well-known` 변경에 반응하지 않아 수동 실행이 필요하다. 이 파일이 없거나 디버그 서명이면 카메라가 먼저 페이지를 열고, 페이지의 **앱에서 열기** 한 번으로 이어진다. |

### Device Hub 계획과의 관계

- **ADR-006 개정**: "stock KDE Connect 앱 우선, 전용 앱은 검증 후" → **모니터링 뷰어는 전용 Homebase 앱, 기기 연동(알림 미러링·공유·배터리·명령)은 계속 KDE Connect(TRAIN C/D)**.
  미결 #4(KDE 장기 방향) 중 "Homebase-native 프로토콜/앱"의 범위가 모니터링으로 정해졌다.
- 페어링 레지스트리 이중화(KDE TLS 페어링 ↔ QR 토큰)는 GOAL-05 `device` 테이블에서 `transport` 구분으로 통합한다(후속).
- 원격 명령은 이 앱 범위가 아니다(읽기 전용 모니터링). ADR-005(raw shell 금지)와 충돌 없음.

## M (계속) — UnifiedPush 1단계 구현, 2026-11-13

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-21 | **UnifiedPush 1단계 구현 세부** | **PC**: 새 채널 `unifiedpush`. router는 정책마다 순서를 다르게 쓴다. `Both`(Critical)는 [local] + [push → Telegram], `TelegramFallback`·`LocalPreferred`는 local → push → Telegram이다. 각 묶음은 첫 성공에서 멈춘다. 새 정책 `EncryptedOnly`(local → push, Telegram 없음)는 Windows 알림 전용이다. `notificationForwarding.telegramFallback: true`일 때만 Telegram으로도 보낸다. 부팅 알림과 Tray 테스트 메시지(파이프 op `notify.test`)도 router를 거친다. Tray 테스트는 Agent에 닿지 않으면 예전처럼 Telegram으로 직접 보내서, 설치 마법사가 계속 동작한다. 엔드포인트는 https만 받는다. 429는 `Retry-After`(기본 60초, 최대 1시간)를 따르고, 404·410이면 구독을 지운다. 토스트는 기본 켜짐이며 앱별 60초 묶음, 24시간에 150건으로 제한한다. **ntfy.sh 실측(2026-11-13, 익명 `GET /v1/account`)**: IP 기준 하루 250건, 메시지 보관 43200초(12시간). UP 게시는 활성 구독자가 없으면 507로 거절되므로 휴대폰(구독자) 기준으로 센다. 그래서 250건은 휴대폰 한 대가 모든 PC와 나눠 쓰는 한도다. 설정에 `blockApps`가 없을 때도 비밀번호 관리자 기본 목록을 쓰고, Tray 필터도 이 설정 목록을 적용한다(예전에는 사용자 제외 목록만 적용했다). **앱**: 커넥터 라이브러리 없이 AND_3 브로드캐스트를 직접 처리한다. 키쌍은 소프트웨어 P-256으로 만들고 Keystore AES-GCM으로 감싼다. 이 키쌍과 엔드포인트 하나를 모든 PC가 함께 쓴다. 알림 채널은 PC별(그룹) × 소스 앱별이다. 배포자가 하나뿐이면 첫 페어링 때 자동으로 켠다. | **Keystore 내부 ECDH(`PURPOSE_AGREE_KEY`)**: API 31+에서만 된다. minSdk 26이라 코드 경로가 둘이 되므로 기각했다. 대가: 개인키가 앱 프로세스 메모리에 잠깐 올라온다. **UnifiedPush 커넥터 라이브러리**: 기능은 같지만 의존성이 생기고(M-3) Tink 등이 딸려 올 수 있어 기각했다. 브로드캐스트 9개는 직접 처리할 만큼 작다. **PC별 키쌍·엔드포인트**: 배포자 등록이 PC 수만큼 늘고 얻는 것이 없어 기각했다. **VAPID**: ntfy는 요구하지 않는다. VAPID를 요구하는 배포자는 `REGISTRATION_FAILED(VAPID_REQUIRED)`로 실패한다(BACKLOG). **토스트 Telegram 기본 대체**: 메신저 내용이 평문 클라우드로 나가므로 기각했다. 대가: 휴대폰을 등록하지 않으면 토스트는 아무 데도 가지 않는다(Agent 로그에만 남는다). 예전에 포워딩을 켜 두고 Telegram으로 받던 사용자는 옵션을 켜야 예전처럼 받는다. |

## M (계속) — 업데이트 알림과 "보는 중" 표시, 2026-11-14

요구(사용자): "업데이트 시에 모바일에서도 알림으로 알려주기", "접속된 기기가 있는 경우에는 상주 알림으로 표현해주기" — 타당성은 맡김.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-22 | **새 버전은 앱이 스스로 확인해 Android 알림으로, "접속 중"은 휴대폰 상주 알림이 아니라 PC 트레이에 표시** | **업데이트 알림**: 앱이 플랫폼 `JobScheduler` 주기 잡(12시간, flex 1시간, 네트워크 필요, 재부팅 뒤 유지 — `RECEIVE_BOOT_COMPLETED`)으로 깨어난다. 확인은 배너와 같은 `UpdateRefresh`를 쓰므로 실제 `update.json` GET은 여전히 하루 1회다. 알릴 버전이 있으면 채널 `hb.app.update`(앱 업데이트)에 알림을 한 번 띄우고, 그 versionCode를 기억해 다시 띄우지 않는다. 알림 권한이 없어 못 띄웠으면 기억하지 않는다. 누르면 앱이 열리고 업데이트 모달이 바로 뜬다. "업데이트 확인"을 끄면 잡과 남은 알림을 지운다. 권한이 없으면 설정 행 아래에 "허용하기" 버튼이 나온다. **접속 표시**: Agent가 이미 Tray에 보내는 `watch on/off`(M-13)로 트레이 아이콘 오른쪽 아래에 초록 점을 찍고, 툴팁과 메뉴 맨 위 한 줄로 "휴대폰이 이 PC를 보는 중"을 알린다. 파이프가 끊기면 끈다. 프로토콜 변경은 없다. | **휴대폰 상주 알림(포그라운드 서비스 + PC별 상시 연결)**: 앱은 화면을 볼 때만 연결한다(M-13). 그래서 상주 알림이 의미를 가지려면 연결을 계속 붙잡아야 한다. 이는 M-19가 기각한 안 그대로다(Doze 중 연결 유지를 위한 배터리 예외, PC 수에 비례하는 배터리, `dataSync` 24시간당 6시간 한도). 또 PC가 계속 "보는 중"이 되어 샘플러·ETW·창 보고가 상시로 돈다(이 PC 실측 한 코어의 약 3%). **PC가 업데이트를 푸시로 알리기**: 트레이 시작 때만 확인하므로 며칠 늦을 수 있다. PC마다 같은 소식을 중복해 보내고 ntfy 일일 한도(M-21)도 쓴다. 앱 업데이트는 앱이 직접 아는 편이 정확하다. **WorkManager**: 잡 하나에 의존성을 들일 이유가 없다(M-3). **대가**: OEM 배터리 정책이 잡을 미룰 수 있어 알림이 늦을 수 있다(배너는 그대로 남는다). 트레이 표시는 몇 대가 보는지가 아니라 "누군가 보는 중"인지만 알린다. |

## M (계속) — PC 경로가 바뀌면 푸시로 알리기, 2026-11-14

증상(사용자 보고): 이미 연결해 둔 PC가 "PC에 닿지 않습니다"로 나오고, QR로 다시 연결하니 됐다.
원인(Agent 로그로 확인): 11-13 20:50 페어링 → 20:52 v0.17.0 업데이트로 Agent 재시작 → Quick Tunnel 주소가 `transport-basin-issue-into` → `angel-four-sets-donation`으로 바뀜.
앱은 `/api/v1/endpoints`를 **연결에 성공한 뒤에만** 읽으므로, 바뀐 주소를 알 길이 없었다. 이 PC의 Wi-Fi가 Windows에서 "공용"이라 LAN 경로는 방화벽에 막히고(M-15), Tailscale 경로로도 붙지 못해(휴대폰 Tailscale이 꺼져 있던 것으로 보인다) 남은 길은 옛 Cloudflare 주소뿐이었다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-23 | **새 경로를 서명된 UnifiedPush로 알린다** | Agent의 `EndpointAnnouncer`가 15초마다 `MobileApiServer.Endpoints()`를 보고, 목록이 두 번 연속 같고(시작 직후 터널이 늦게 뜨는 것을 기다림) **이전에 알리지 않은 경로가 있을 때만** 푸시 구독이 있는 기기마다 경로 목록을 보낸다. 경로가 빠지기만 한 경우(Wi-Fi 끊김)는 보내지 않는다(ntfy 하루 250건 한도). 알린 목록은 휴대폰마다 따로 기억해, 받지 못한 휴대폰에만 5분 뒤 다시 보낸다(나중에 푸시를 등록한 휴대폰도 한 번 받는다). 평문은 `{schema, kind:"endpoints", pcId, data, mac}`이고 `data`는 `{schema, pcId, ts, endpoints}`의 base64url, `mac`은 그 바이트의 HMAC-SHA256이다. 키는 그 기기 토큰의 SHA-256(원시 32바이트)이다 — PC는 해시만 저장하므로 그대로 쓸 수 있다. 앱은 MAC과 안팎 `pcId` 일치를 확인하고, 마지막으로 반영한 푸시의 `ts`보다 새것만 받아 저장한다(늦게 온 중복이 옛 경로를 되살리지 않게, PC 시계가 뒤로 간 경우는 M-40). 알림은 띄우지 않는다. 재시도 중인 상세 화면은 매 시도마다 저장된 항목을 다시 읽어 새 경로를 쓴다. 옛 앱은 `id`·`title`이 없어 버린다. | **복호화만으로 신뢰**: 휴대폰 푸시 키는 모든 PC가 공유하므로(M-21) 한 PC가 다른 PC의 경로를 자기 주소로 바꿔 그 PC 토큰을 받아 낼 수 있다 → MAC으로 막는다. **경로가 바뀔 때마다 전송**: Wi-Fi가 오락가락하면 한도를 쓴다. **Quick Tunnel 주소 고정**: Cloudflare가 지원하지 않는다(고정 주소는 이름 있는 터널, 도메인 필요). **대가**: 휴대폰에서 푸시가 꺼져 있으면(배포자 없음) 여전히 다시 페어링해야 한다. 상세 화면은 이 경우 그렇게 안내한다. Agent 재시작마다 푸시가 한 건 나간다. |
| M-24 | **같은 휴대폰을 다시 페어링하면 옛 기기 항목을 PC가 자동 철회** | 푸시 엔드포인트는 앱 설치 하나에 하나이고, 앱은 PC마다 토큰을 하나만 둔다(재페어링은 항목을 교체). 그러니 같은 엔드포인트를 가진 다른 기기 항목은 그 휴대폰의 예전 페어링이다. `MobileDeviceStore.SetPushSubscription`이 새 구독을 저장할 때 같은 엔드포인트의 다른 기기를 철회하고, 시작할 때도 이전 버전이 남긴 중복을 정리한다(가장 최근 페어링만 남김). 실제로 11-14 재페어링 뒤 이 PC에 S24가 두 번(`d71d4be20190`, `34dd104ea994`) 남아 알림 푸시가 두 번씩 나갔다. | **페어링 요청에 옛 토큰을 실어 PC가 철회**: 푸시가 꺼진 휴대폰도 정리되지만 프로토콜·앱 변경이 커서 보류. 대가: 푸시를 끈 휴대폰의 재페어링 중복은 Tray에서 직접 지워야 한다(목록에만 남고 알림 중복은 없다). **기기 이름으로 판단**: 이름이 같은 다른 휴대폰을 지울 수 있어 기각. |
| M-25 | **받을 알림 범주를 휴대폰이 고르고, PC가 보내기 전에 거른다** | 앱 설정 → 알림 → "받을 알림"에 그룹 5개(Windows 앱 알림 / 감시 경보 = 프로세스·서비스·HTTP·포트 / 이벤트 로그 / PC 상태 조언 / 부팅) 스위치와 프리셋 2개(모두 받기, Windows 알림만)를 둔다. 앱은 **끈 `category` 목록**을 `POST /api/v1/push`의 `mutedCategories`로 모든 PC에 보낸다(선택이 바뀌면 즉시 재등록, 동기화 키에 포함). PC는 그 목록을 구독과 함께 저장하고 `UnifiedPushOutboundChannel`이 범주를 끈 휴대폰을 건너뛴다. 모든 휴대폰이 끈 범주는 **성공**으로 돌려 라우터가 Telegram으로 넘기지 않는다(끈 것은 실패가 아니라 선택). Tray 테스트(`test`)는 끌 수 없다. 옛 PC를 위해 앱도 받은 메시지를 한 번 더 거른다. 선택은 휴대폰 하나에 하나(모든 PC 공통)다. | **앱에서만 거르기**: 구현은 작지만 ntfy 하루 250건 한도(M-21)를 끈 알림이 그대로 쓴다. **PC(Tray)에서 범주 고르기**: 휴대폰마다 다르게 받을 수 없고, 받는 쪽이 정하는 편이 자연스럽다. **켠 범주 목록을 보내기**: PC에 새 범주가 생기면 옛 앱이 그걸 영영 못 받는다 → 끈 목록을 보내 새 범주는 기본으로 받게 했다. **PC별 범주 선택**: Android 채널(PC 그룹 × 앱)로 이미 PC·앱 단위로 끌 수 있어 중복이다. **대가**: 앱 그룹과 PC 범주 이름이 묶여 있어 PC가 범주를 추가하면 앱 그룹 목록(`NotificationGroups`)도 고쳐야 그 범주를 끌 수 있다(그 전까지는 받는다). |

## M (계속) — 여러 PC·여러 휴대폰이 서로 상태·알림을 나누기, 작업 탭 확장, 2026-11-14

요구(사용자): PC 여러 대와 휴대폰 여러 대가 서로의 상태를 확인하고 각자에게 온 알림을 서로 전달받으며, 한 기기에서 다른 기기들을 이어서 관리한다. 작업 탭에는 Claude Code 말고도 작업이라 부를 만한 것(활성 탭, Office 문서, Codex 등)을 넣는다.
확인한 사실: 앱은 이미 PC 여러 대를, PC는 이미 휴대폰 여러 대를 페어링한다(1:1이 아니다). 빠진 것은 ① PC↔PC 연결, ② 휴대폰 상태(배터리 등)를 다른 기기가 보는 길, ③ 휴대폰 알림을 다른 기기로 보내는 길, ④ PC 알림을 다른 PC가 받는 길이다.
이 PC 실측(작업 후보): Codex 데스크톱이 `~/.codex/sessions/**/rollout-*.jsonl`(심볼릭 링크 `D:\Users\jeiel\Caches\Home\.codex`)에 세션을 남기고 `session_index.jsonl`에 `thread_name`이 있다. Gemini/Antigravity CLI 기록에는 제목이 없고 프롬프트 원문뿐이다. 창으로 보이는 작업은 Chrome·Slack·KakaoTalk·ChatGPT·Claude 데스크톱 등.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-26 | **작업 탭 = 일반 작업 목록 `work`** | `LiveSnapshot`에 `work: [{kind, app, title, detail?, lastActivity?, foreground}]`를 더한다. `kind`는 `agent`(AI 코딩 세션) · `browser`(브라우저 활성 탭) · `document`(Office·한글 문서) · `editor`(코드 편집기·IDE). **agent**: Tray가 Claude Code(M-9)와 **Codex**(`$CODEX_HOME` 또는 `~/.codex`의 최근 10분 안에 바뀐 `sessions/**/rollout-*.jsonl` 꼬리 64KB에서 `cwd`만, 제목은 `session_index.jsonl`의 `thread_name`)를 읽어 `SessionReport.agents`로 보낸다. 프롬프트·응답 본문은 읽지 않는다. **창 기반**: Agent가 이미 받는 창 목록을 프로세스 이름으로 분류하고 창 제목 끝의 앱 이름(" - Google Chrome", " - Word" 등)을 떼어 제목으로 쓴다. 표는 `WorkCatalog`에 있다. 옛 앱을 위해 `claudeSessions`는 그대로 둔다. | **Gemini/Antigravity 기록 읽기**: 제목 없이 프롬프트 원문만 있어 M-9(본문을 읽지 않음)에 어긋난다 → 기각, Antigravity 창이 있으면 창 기반으로만 보인다. **브라우저 확장으로 모든 탭 읽기**: 설치·권한이 늘고 브라우저마다 따로 만들어야 한다 → 활성 탭(창 제목)만. **앱에서 분류**: 표가 바뀔 때마다 앱 업데이트가 필요하다 → PC에서. 대가: 창 제목은 앱 탭에도 이미 나오므로 새로 드러나는 정보는 Codex 세션 제목과 폴더뿐이다. 창 제목 형식이 다른 앱(언어 팩 등)은 접미사가 남을 수 있다. |
| M-27 | **PC가 허브: 페어링한 기기(휴대폰·다른 PC)의 상태를 모아 보여 준다** | 새 `POST /api/v1/presence`(인증): 기기가 자기 상태 `{schema, kind: phone\|pc, appVersion, battery?, charging?, network?, cpuPct?, memUsedBytes?, memTotalBytes?, uptimeSec?, pushHashes?}`를 보내고 응답으로 `DevicesResponse {schema, hub, devices[]}`(허브 PC 자신과 페어링한 모든 기기의 마지막 상태·`lastSeenAt`·`online`)를 받는다. `GET /api/v1/devices`도 같은 응답이다. 상태는 메모리에만 두고(재시작하면 비어 다음 보고로 채워짐), 2분 넘게 보고가 없으면 `online=false`. 휴대폰은 앱을 열 때·새로고침할 때·알림을 보낼 때와 30분 주기 잡에서, 다른 PC는 30초마다 보고한다. 허브 자신의 CPU·메모리는 `GetSystemTimes`·`GlobalMemoryStatusEx`로 직접 재므로 샘플러(M-5)를 깨우지 않는다. `PairRequest`에 `kind`(기본 phone)를 더해 기기 목록에 PC/휴대폰을 구분해 저장한다. | **기기끼리 직접(메시) 연결**: 휴대폰은 서버가 없어 들어오는 연결을 못 받는다 → PC를 허브로. **상태를 디스크에 저장**: 순간값이라 재시작 뒤 낡은 값이 "온라인"처럼 보일 위험만 있다. **대가**: 기기는 자기가 페어링한 허브를 통해서만 서로를 본다. 휴대폰 상태는 주기 잡이 OEM 배터리 정책에 밀리면 늦을 수 있다(그 경우 `lastSeenAt`으로 드러난다). |
| M-28 | **알림 중계: 기기 → 허브 → 허브의 다른 기기, 한 단계만** | 새 `POST /api/v1/notifications`(인증) `{schema, id, app, title, body?, ts}`: 휴대폰(알림 접근 권한, 기본 꺼짐)이나 다른 PC(자기 Windows 알림)가 허브로 보낸다. 허브는 마스킹(M-19) 뒤 Windows 알림 묶음 파이프라인(앱·출처별 60초 묶음, 하루 150건)에 새 범주 **`device-notification`**으로 넣어 ① 자기 트레이에 Windows 알림으로 띄우고 ② 푸시로 다른 휴대폰에 보낸다. **보낸 기기와, 보낸 기기가 `pushHashes`로 알린 자기 휴대폰(푸시 엔드포인트 SHA-256)은 건너뛴다**(같은 휴대폰이 두 PC에 붙어 있어도 한 번만). 다른 PC에는 새 `GET /api/v1/inbox?after=<seq>`(롱 폴링 25초)로 준다: 허브의 Windows 알림과 기기에서 받은 알림 최근 200건·30분, 요청한 기기가 보낸 것은 빼고. 허브에서 받은 알림은 받은 PC가 자기 트레이와 자기 휴대폰(허브의 `pushHashes`에 있는 휴대폰 제외)에만 보내고 **다시 중계하지 않는다**(`hops`=1, 되돌아오는 고리 없음). 트레이는 자기가 띄운 알림을 Windows 알림 수집에서 뺀다. 감시 경보·부팅 알림은 중계하지 않는다(각 PC가 자기 휴대폰에 보낸다). | **휴대폰 쪽 중복 제거만**: 같은 알림이 ntfy 하루 한도(M-21)를 두 번 쓴다 → 보내는 쪽에서 엔드포인트 해시로 거른다. **여러 단계 중계**: 고리와 중복 처리가 커진다. 대가: PC가 셋 이상이면 A의 알림은 A에 직접 붙은 PC에만 간다(그 PC의 다른 허브로는 가지 않는다). 중계된 휴대폰 알림도 받는 휴대폰의 ntfy 한도(250건/일)와 허브의 토스트 한도(150건/일)를 함께 쓴다. 휴대폰이 PC에 닿지 않으면(경로 없음) 그 알림은 버린다(대기열 없음 — 알림은 금방 낡는다). |
| M-29 | **PC↔PC 연결 = 다른 PC를 기기로 페어링** | 트레이 설정 → 휴대폰·기기 탭 → "다른 PC 연결"에 상대 PC의 페어링 링크(M-20, 상대 트레이의 "링크 복사")를 붙여 넣으면 Agent가 `kind: pc`로 페어링하고 허브 정보 `{pcId, pcName, endpoints, token}`을 `hubs.json`(토큰은 DPAPI LocalMachine 암호화, ProgramData ACL 그대로)에 저장한다. `HubLinkService`가 허브마다 30초 주기 상태 보고(M-27), 자기 Windows 알림 전송·허브 inbox 롱 폴링(M-28), 경로 갱신(`/api/v1/endpoints`)을 한다. 경로는 저장 순서대로 시도하고 LAN은 인증서 SHA-256을 고정 검증한다(앱과 같은 규칙). 연결은 한쪽 방향이지만 데이터는 양방향이다(B가 A에 붙으면 A는 B의 상태·알림을 받고, B는 A의 상태·알림을 받는다). 연결 해제는 새 `DELETE /api/v1/device`(자기 철회, 휴대폰도 사용 가능)를 부른 뒤 로컬 항목을 지운다. 트레이 대시보드에 "기기" 섹션을 두어 이 PC에 붙은 기기와 연결한 PC(및 그 PC에 붙은 기기)의 상태를 보여 준다. | **새 PC 전용 페어링**: 코드·잠금·인증 경로가 중복된다 → 휴대폰 페어링을 그대로 쓴다(PC도 같은 권한: 실시간 보기 포함). **트레이에서 다른 PC 실시간 보기**: WPF로 앱 수준의 화면을 다시 만들어야 한다 → 요약(CPU·메모리·가동 시간·온라인)만, 자세히는 휴대폰 앱. 대가: 상대 PC가 Quick Tunnel만 열려 있으면 재시작마다 주소가 바뀌어 끊긴다(휴대폰은 M-23 푸시로 따라가지만 PC는 그 푸시를 받지 않는다) → LAN·Tailscale·고정 터널을 권장하고 끊기면 다시 연결해야 한다. |

## M (계속) — PC끼리 24단어 연결 코드, 2026-11-14

요구(사용자): PC↔PC 연결을 Brave 동기화처럼 단어 코드를 다른 PC에 붙여 넣는 방식으로도. 확인: Brave의 코드는 키만 담고 기기는 Brave 서버에서 만난다. Homebase는 서버가 없으므로(M-1) 코드에 "상대를 찾을 단서"까지 담고, 받는 PC가 직접 찾는다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-30 | **24단어(BIP39 영어) 연결 코드 = 페어링 오퍼의 요약, 받는 PC가 LAN·Tailscale에서 상대를 찾는다** | 32바이트를 BIP39 방식(256비트 + SHA-256 첫 바이트 8비트 체크섬)으로 24단어로 바꾼다. 내용: 버전(1) · `pcId` SHA-256 앞 4바이트(상대 식별) · LAN 인증서 SHA-256 앞 8바이트(없으면 0) · 포트(2바이트) · 일회용 페어링 코드 16바이트(QR의 `code`와 같은 값, 5분·1회) · 예약 1바이트. 트레이가 지금 오퍼에서 만들어 "코드 복사"로 준다(프로토콜 변경 없음). 받는 PC의 Agent는 ① `tailscale status --json`의 온라인 피어(100.x)와 ② 자기 LAN 주소와 같은 /24(서브넷이 더 좁으면 그 범위) 호스트에 `GET /api/v1/info`를 동시에 짧게(연결 1.5초) 물어, `pcId` 식별값이 맞는 PC를 고른다. LAN은 TLS 인증서 SHA-256 앞 8바이트가 코드와 맞을 때만 접속한다(64비트 — 같은 LAN의 가짜 PC가 맞추기 어렵다). 찾으면 그 경로로 `POST /api/v1/pair {kind: pc}` 후 `GET /api/v1/endpoints`로 전체 경로(Cloudflare 포함)를 받아 저장한다 — 이후는 M-29와 같다. 단어 목록은 `bitcoin/bips` 원본(SHA-256 `2f5eed53…dbda`)을 Core에 넣었다. 링크 붙여넣기도 그대로 된다(같은 칸에 둘 다 받음). | **12단어(128비트)**: 비밀만 담으면 상대를 못 찾고, 비밀을 줄이면 1회용 코드가 약해진다. **오퍼 전체를 단어로**: 경로·지문까지 넣으면 수백 단어. **UDP 브로드캐스트 탐색**: 응답할 리스너와 방화벽 규칙(관리자)이 새로 필요하다 → 이미 열린 47820 HTTP(S)를 묻는다. **한글 BIP39 목록**: 유니코드 정규화 차이로 입력이 어긋나기 쉽다. **Brave식 '체인'(코드 하나로 모든 기기가 서로 연결)**: 모든 기기 자동 상호 연결이라는 큰 구조 변경 → 기각, 연결은 여전히 PC 한 쌍씩. **대가**: Cloudflare로만 닿는 PC(다른 네트워크, Tailscale 없음)는 코드로 못 찾는다 → 링크를 쓴다. 코드는 5분 안에 붙여 넣어야 한다(QR과 같은 수명). LAN 탐색은 같은 /24 안에서만 한다. |

## M (계속) — 알림 센터(받은 푸시 이력), 2026-11-14

요구(사용자): 받은 푸시 알림의 이력을 확인하는 알림 센터. Android 알림 창은 지우면 사라지고, 알림 권한이 꺼져 있으면 아무것도 남지 않는다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-31 | **알림 이력은 휴대폰에만, 받은 푸시를 기록** | 앱이 받아들인 푸시(복호화·페어링·끈 범주·중계 중복 필터를 통과한 것)를 `PushReceiver`에서 기록한다. Android 알림을 띄우지 못한 경우(권한 꺼짐)도 `shown=false`로 남긴다. 저장은 앱 전용 파일 `files/push_history.enc` 하나: JSON(`{v:1, entries[]}`)을 Keystore AES-GCM 키(`homebase.push.history`, PC 토큰과 같은 방식)로 암호화하고 `AtomicFile`로 쓴다(백업은 매니페스트에서 꺼져 있음). 최근 **30일·최대 500건**만 남긴다. 읽음 표시는 `lastSeenAt` 하나(알림 센터를 열면 그때까지 받은 것 모두 읽음). 앱에서 PC를 지우면 그 PC의 이력도 지운다(알림 채널과 같음). 경로 푸시(M-23)는 알림이 아니라 기록하지 않는다. 프로토콜·PC 변경 없음. | **PC가 보낸 알림을 PC에서 조회(`GET /api/v1/...`)**: 여러 PC의 이력을 휴대폰이 모아야 하고, 휴대폰이 받지 못한 알림까지 보여 "받은 푸시"와 어긋나며, PC마다 본문(메신저 내용)을 더 오래 보관하게 된다 → 휴대폰 기록. **Room/SQLite**: 수백 건 목록 하나에 의존성·마이그레이션이 과하다 → 파일 하나. **평문 SharedPreferences**: 전달된 Windows 알림 본문이 들어가므로 암호화한다. 대가: 휴대폰을 바꾸거나 앱 데이터를 지우면 이력은 사라진다(Keystore 키가 없으면 읽지 못해 빈 이력으로 다시 시작). 끈 범주의 알림은 PC가 보내지 않으므로 이력에도 없다. |

## M (계속) — PC 알림 센터, 2026-11-15

요구(사용자): 휴대폰처럼 PC에서도 연결된 기기에서 온 알림 이력을 보고 싶다. 설계안 [`docs/PC_NOTIFICATION_HISTORY.md`](docs/PC_NOTIFICATION_HISTORY.md)를 승인받아 구현.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-32 | **PC도 받은 알림을 Agent DB에 30일 보관하고 트레이 '알림 센터'로 보여 준다 (M-31 일부 번복)** | 다른 기기에서 중계된 알림(휴대폰 알림 전달, 연결한 PC의 Windows 알림)을 `NotificationForwardingService.AcceptFromDeviceAsync`에서 중복 제거·마스킹 직후, 풍선을 띄우기 전에 SQLite V2 표 `notification_history`에 기록한다(보낸 기기 이름·종류 phone/pc 포함). 이 PC의 경보는 기존 `alert_log`를 그대로 읽어 함께 보여 준다. 보관은 30일·중계 알림 최대 500건, `alert_log`도 30일(하루 한 번 `NotificationHistoryMaintenance`). 트레이는 브리지 연산 `history.list {after?, limit≤100}`(다음 페이지 기준 = 시각·종류·id, 본문 2,000자, 응답은 직렬화 512KB 이내로 오래된 쪽을 덜어 냄)·`history.clear`(중계 알림만, 경보는 진단용으로 남김)로 읽고, 메뉴 "알림 센터 (N)"·대시보드 버튼·풍선 클릭으로 창을 연다. 읽음은 Windows 사용자별 `%LOCALAPPDATA%\Homebase\notification-center-seen.txt` 하나(처음엔 '지금'). 본문을 저장하므로 서비스(LocalSystem)가 시작할 때 `ProgramData\Homebase\data` 폴더를 SYSTEM·Administrators 전용으로 바꾼다(전에는 Users 읽기 가능). | **이 PC 자신의 Windows 알림도 기록**: Windows 알림 센터가 이미 보여 주고 메신저 본문이 한 벌 더 남는다 → 제외. **트레이(사용자별 파일)에 저장**: 아무도 로그인하지 않았을 때 온 알림이 빠진다 → Agent. **본문 별도 암호화(DPAPI LocalMachine)**: 같은 PC의 다른 프로세스를 막지 못해 파일 ACL과 실익이 같다 → ACL. **대가**: 중계 알림 본문(마스킹 후)이 PC에 30일 남는다. 같은 PC의 다른 Windows 사용자도 창을 열 수 있다(풍선이 이미 그 사용자에게 뜨므로 노출 범위는 같다). 휴대폰·기기 연결이 꺼져 있으면 브리지가 없어 경보도 볼 수 없다. 설계 번들의 V2(GOAL-05)는 이제 V3가 된다. |

## M (계속) — 배포 채널 분리: GitHub 빌드와 Google Play 빌드, 2026-11-16

요구(사용자): GitHub(스탠드얼론 APK)와 Google Play는 채널마다 별도 빌드로 낸다. 각 스토어의 업데이트 방식을 따르고, 키스토어도 채널마다 다르게 쓴다.
Play Console 앱(`io.github.jeiel85.homebase`, 2026-11-15 등록)에 첫 빌드를 올린다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-33 | **product flavor `channel` = `github` · `play`, 채널별 서명 키, Play 빌드에는 자체 업데이터 없음 (M-4 일부 번복)** | `github`은 지금까지의 APK 그대로다(인앱 업데이트, `REQUEST_INSTALL_PACKAGES`, 업데이트 확인 잡, FileProvider). 이 코드는 `src/github/`에만 있고, `main`은 `update.UpdateSurface` 하나만 부른다. `play`는 같은 이름의 빈 구현(`src/play/`)을 컴파일하므로 업데이트 확인·다운로드·설치 코드와 그 권한·서비스가 AAB에 아예 없다(Play 기기·네트워크 악용 정책: Play 밖의 방법으로 자기 자신을 업데이트하지 않는다). 설정의 "업데이트" 섹션도 Play 빌드에서는 그리지 않는다. 서명: `github` = 기존 릴리스 키(M-12, `HOMEBASE_RELEASE_*`), `play` = 새 **Play 업로드 키** `homebase-play-upload.jks`(PKCS12, RSA 4096, alias `homebase-upload`, 인증서 SHA-256 `a6aefcdda18a7f5935e63066d14cfa4e9b47db40048242f7bb87c12e33b44a5e`, `HOMEBASE_PLAY_UPLOAD_*`, 로컬 원본 `D:\Project\homebase\.keystore\`, 백업 `G:\내 드라이브\모바일 앱 개발\Homebase\릴리즈 키스토어\` cmp 일치). Play가 앱 서명 키(Play App Signing)로 다시 서명해 배포한다. applicationId·versionCode 규칙(M-4)은 두 채널이 같다. CI 릴리스는 `github`만 만들고, Play AAB는 관리자 PC에서 만들어 올린다(`docs/PLAY_RELEASE.md`). 같은 변경에서 compileSdk·targetSdk를 36으로 올렸다(Play: 2026-08-31부터 신규 앱·업데이트는 API 36 이상). | **한 빌드에서 설치 출처(`getInstallSourceInfo`)를 보고 업데이터를 끄기**: 코드와 권한이 Play 빌드에 남아 정책 심사에서 걸릴 수 있고, 사용자 지시(채널별 빌드)와 다르다 → 기각. **두 채널 같은 키**: GitHub 키가 새면 Play 업로드까지 위험해지고, 업로드 키는 Play 지원으로 재설정할 수 있지만 GitHub 키는 그렇지 않다 → 분리. **applicationId를 채널마다 다르게**: 두 앱을 동시에 깔 수 있지만 Play Console 앱이 이미 이 패키지로 등록됐고 페어링 App Link(M-20)도 이 패키지 기준 → 같게 유지. **대가**: 서명이 달라 채널끼리 덮어 설치할 수 없다. 채널을 바꾸려면 지우고 다시 설치해야 하고, 백업을 끄고 있으므로(`allowBackup=false`) PC 연결도 다시 해야 한다. Play판에서 페어링 링크가 '확인된 App Link'가 되려면 `jeiel85.github.io/.well-known/assetlinks.json`에 Play 앱 서명 인증서 지문을 더해야 한다(그 전에는 설치 안내 페이지의 '앱에서 열기' 버튼 경로로 열린다). |

## M (계속) — 하드웨어 패널: GPU·드라이브·온도·핑, 2026-11-16

요구(사용자): 포크해 둔 PhoneBox(ersinkoc/phonebox, MIT)의 PC 모니터링 기능 중 1순위(GPU, 디스크, 온도, 핑, CPU 이름, 이력 그래프)를 휴대폰 상세 화면으로 가져온다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-34 | **`LiveSnapshot.hardware`(선택 필드) + 보는 동안만 도는 `IHardwareSampler` (M-5 유지)** | GPU는 커널 드라이버 없이 Windows가 주는 값으로 읽는다: D3DKMT(어댑터·VRAM 총량·온도·전력 비율·팬·메모리 클럭)와 영문 이름의 GPU 성능 카운터(부하·VRAM 사용량, 작업 관리자와 같은 출처). NVIDIA는 `nvidia-smi -lms 1000` 하나를 보는 동안만 띄워(Job Object로 Agent와 함께 죽음) 전력·클럭·팬을 더하고, 두 출처는 이름으로 합친다(앞 출처 우선, 빈 값만 채움). 드라이브는 2 GB 넘는 고정 드라이브, 온도는 `/status`와 같은 `ITemperatureCollector`(기본 WMI, opt-in LHM), 핑은 2초마다 `mobile.pingHost`(기본 1.1.1.1, 빈 값이면 끔)로 최근 30회의 평균·손실. 읽지 못한 값은 0이 아니라 null로 보내 앱이 빼고 그린다. 스키마 번호는 그대로(1): 필드 추가는 옛 앱이 무시한다. PhoneBox 코드를 옮긴 부분은 `THIRD-PARTY-NOTICES.txt`(Agent 출력 폴더에 함께 배포)에 MIT 고지를 남긴다 | LibreHardwareMonitor로 GPU 읽기 — WinRing0 취약 드라이버 문제(기본값 아님)라 GPU 기본 출처로 삼지 않음. 상시 수집 + 이력 보관 — M-5(보지 않을 때 부하 0) 위반, 휴대폰 화면의 60초 이력이면 충분. 대가: 노트북의 NVIDIA 외장 GPU는 nvidia-smi 질의로 깨어나므로, 휴대폰이 보고 있는 동안에는 절전(D3cold)에 들지 못해 배터리를 더 쓴다. 보지 않으면 nvidia-smi도 없다 |
| M-35 | **네트워크 합계는 실제 유선·Wi-Fi 어댑터만** | `system.netRxBps/netTxBps`에서 Tailscale·VPN(TAP·WireGuard·ZeroTier)·Hyper-V/WSL(vEthernet)·VMware·VirtualBox·Bluetooth·Wi-Fi Direct 가상 어댑터를 뺀다. 이 트래픽은 물리 어댑터에도 지나가므로 함께 더하면 두 번 센다. 이 PC에서 확인: Tailscale·VMware VMnet1/8·Hyper-V Default Switch 어댑터가 Up 상태로 Wi-Fi와 함께 합계에 들어가 있었다(휴대폰이 Tailscale로 볼 때 그 트래픽은 Tailscale과 Wi-Fi 양쪽에서 센다) | 가상 어댑터별 표시 — 앱은 PC 전체 속도만 보여 주므로 필요 없음. 대가: 가상 스위치만 쓰는 특수 구성(물리 NIC 없이 VM 트래픽만)은 0으로 보인다 |

## M (계속) — PC 별칭, 2026-11-16

요구(사용자): PC마다 별칭을 지정하고 싶다. 앱 목록에는 `DESKTOP-V0JCEPJ` 같은 Windows 컴퓨터 이름이 그대로 보인다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-36 | **별칭은 휴대폰 앱에서 PC마다 정하고, 그 휴대폰에만 둔다** | PC 카드 메뉴 "별칭 지정"(정한 뒤에는 "별칭 바꾸기")에서 입력한다. `PcEntry.alias`로 `homebase_pcs` 항목에 함께 저장한다(필드 추가뿐이라 옛 항목은 별칭 없음으로 읽힌다). 앞뒤 공백을 지우고 안쪽 공백·줄바꿈은 한 칸으로 줄이며 40자(코드 포인트)까지 둔다. 비우거나 PC 이름과 같으면 별칭 없음이다. 같은 PC를 다시 페어링해도 별칭은 남는다. 앱이 PC 이름을 보여 주는 곳은 모두 `displayName`(별칭, 없으면 PC 이름)을 쓴다: PC 카드, 상세 화면 제목, 푸시 알림의 보조 텍스트와 Android 알림 설정의 PC 그룹 이름, 알림 센터, "다른 기기"의 "○○ 경유". 별칭이 있으면 카드 메타 줄과 상세 화면 위 작은 글씨에 PC 이름을 같이 보여 준다. 알림 센터 이력 파일에는 PC가 보낸 이름을 그대로 두고 그릴 때 바꾸므로, 별칭을 바꾸면 지난 알림도 새 이름으로 보인다. 별칭이 없을 때 PC가 보내는 것(푸시·이력·허브 이름)은 PC가 지금 보낸 이름을 쓴다(`shownName`): 저장된 `name`은 페어링 때 값이라 Windows에서 PC 이름을 바꾸면 옛 이름이 남기 때문이다. PC·프로토콜은 바뀌지 않는다. | **PC 쪽 표시 이름(트레이 설정에서 정하고 모든 휴대폰·PC에 전파)**: Windows에 이미 "PC 이름 바꾸기"가 있어 같은 일을 하는 설정이 하나 더 생기고, Agent·트레이·프로토콜(페어링·`/info`·스냅숏·푸시의 `pcName`)과 앱 갱신이 모두 필요하다 → 기각. **대가**: 별칭은 휴대폰마다 따로다. 다른 휴대폰이나 다른 PC의 트레이 "기기" 목록, Telegram 메시지에는 여전히 Windows 컴퓨터 이름이 보인다. 앱을 지우거나 다른 채널로 바꿔 설치하면(백업 꺼짐) 별칭도 다시 정해야 한다. |

## M (계속) — 알림 묶음과 우선순위, 2026-11-16

요구(사용자): 휴대폰에서 다른 기기 알림을 받으면 목록이 바로바로 떠서 스크롤이 많이 생긴다. 알림 우선순위(팝업 여부, 소리/진동 여부, 모음 여부 등)를 지정하고 싶다.
원인: 푸시마다 `setGroup`만 걸고 그룹 요약을 올리지 않아 Android가 묶지 않았고(그룹 키가 있으면 자동 묶음도 안 된다), `device-notification`은 `isToast`가 아니라서 PC 경보처럼 IMPORTANCE_HIGH(헤드업+소리)로 떴다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-37 | **휴대폰 앱에 종류별·앱별 알림 프로필(알림 창 표시·팝업·소리·진동·묶음), 묶음은 그룹 요약 + 알림 센터 묶음 카드 (M-19 채널 규칙 확장)** | 프로필 `AlertProfile{shade, popup, sound, vibrate, bundle}`를 `homebase_notification_rules` SharedPreferences에 종류(`NotificationGroups` 키)별·앱별(`<category>\|<source 소문자>`, Windows 알림·다른 기기 알림만)로 둔다. 적용 순서: 앱별 → 종류별 → 기본값(앱 알림 = 보통, PC 경보·모르는 범주 = 긴급, 모두 묶음). PC 트레이의 `test`는 경로 확인용이라 늘 팝업. 단계 4개(긴급·보통·조용히·센터에만)는 팝업·소리·진동만 정하고 묶음은 따로 둔다. Android는 채널의 중요도·소리·진동을 만든 뒤 앱이 바꿀 수 없으므로 채널 id = `hb.<pcId>.<source 해시>.<h\|d\|l><소리><진동>`(팝업=HIGH, 소리나 진동=DEFAULT, 둘 다 없음=LOW)로 조합마다 따로 만들고, 같은 PC·출처의 다른 채널(옛 단일 채널 포함)은 지운다. 같은 PC·출처 채널 중 하나라도 사용자가 Android 설정에서 꺼 두었으면(IMPORTANCE_NONE) 그대로 꺼진 채로 두고 띄우지 않는다(`shown=false`). 묶음: 그룹 키 = 출처(다른 기기 알림은 PC와 무관하게 기기·앱, 그 밖은 PC+출처). 2건부터 요약(InboxStyle 5줄, `GROUP_ALERT_CHILDREN`이라 요약은 울리지 않음, 누르면 알림 센터)을 올리고, 알림 창에는 묶음당 최근 8건만 남긴다(나머지는 알림 센터에). 자식을 하나씩 지워 요약만 남으면 Android가 요약을 단독으로 보여 주므로, 자식의 삭제 인텐트(내보내지 않는 `NotificationDismissReceiver`)와 앱 복귀 때(`onResume`, 1.5초 뒤 한 번 더) 자식 없는 요약을 지운다. 알림 센터는 하루 안 같은 묶음 키의 이력을 가장 새 것 자리에 한 카드로 접는다(미리보기 3줄, 펼치면 들여 쓴 원래 카드). "센터에만"은 Android 알림을 올리지 않고 이력에 `quiet=true`로 남긴다. 앱이 열려 있을 때 누른 푸시도 그 PC·알림 센터로 이동한다. PC·프로토콜 변경 없음. | **PC가 받을 휴대폰별 우선순위를 알아서 보내기(프로토콜에 priority 확장)**: 같은 알림을 여러 휴대폰이 다르게 보고 싶을 수 있고, 표시 방식은 휴대폰 사정이라 휴대폰에 둔다. **우선순위 4개 채널 고정(PC·앱 구분 없이)**: M-19의 PC·앱별 끄기를 잃는다 → PC·출처 채널에 조합 접미사. **한 알림을 InboxStyle로 계속 고쳐 쓰기(묶음 = 알림 1개)**: 낱개로 지우거나 펼칠 수 없고 매번 다시 울리게 하려면 알림을 새로 써야 한다 → Android 표준 그룹 요약. **묶음 한도 없음**: 앱당 활성 알림 약 50개 한도에 걸려 새 알림이 버려질 수 있다 → 8건. **대가**: 앱에서 우선순위를 바꾸면 그 앱·PC의 채널이 새로 생겨 Android 설정에서 따로 바꿔 둔 소리 등은 처음 상태가 되고, Android 앱 알림 설정 아래에 "삭제된 카테고리 N개"가 보일 수 있다. 같은 id로 다시 만들면 Android가 지웠던 채널의 설정을 되살린다. 다른 기기 알림이 기본으로 더는 헤드업으로 뜨지 않는다(사용자가 긴급으로 되돌릴 수 있음). |

## M (계속) — 알림에서 PC 구분, 2026-11-16

요구(사용자): 모바일 알림이 왔을 때 어느 PC에서 온 것인지 쉽게 구분되고, 알림 내용이 더 잘 읽혔으면 한다.
원인: PC 이름은 Android 알림 머리줄의 작은 보조 텍스트(`setSubText`)에만 있었고, 굵은 제목은 PC가 보낸 "카카오톡: 홍길동"이라 앱 이름이 제목 앞을 차지했다. 앱 이름은 알림 어디에도 따로 없었다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-38 | **PC마다 색·머리글자 배지, 알림 머리줄은 "보낸 곳 · 앱(또는 감시 종류)", 제목은 알림 내용만 (휴대폰 앱만)** | PC마다 팔레트 8색(빨강 제외, 흰 글씨가 읽히는 진한 색) 중 하나를 `PcEntry.color`(팔레트 번호)로 `homebase_pcs`에 저장한다. 처음 정할 때 `pcId` 해시 칸에서 시작해 다른 PC가 쓰지 않는 첫 색을 고르고, 한 번 정한 색은 다른 PC를 추가·재페어링·삭제해도 바뀌지 않는다(재페어링도 별칭처럼 색을 이어받음). 색 필드가 없는 옛 항목은 읽을 때 목록 순서로 정해지고 다음 저장 때 기록된다. 8대를 넘으면 색을 나눠 쓴다. 머리글자는 별칭(없으면 PC 이름)의 첫 글자(이모지·한글 음절은 통째로, 라틴 문자는 대문자). Android 알림에는 그 색 원 + 흰 글자를 큰 아이콘(`setLargeIcon`)으로, 같은 색을 `setColor`로 넣는다. 보조 텍스트는 "작업실 · 카카오톡"/"작업실 · 프로세스 감시"(Android가 뒤에서부터 줄이므로 보낸 곳이 남음), 제목은 PC가 붙인 "<앱>: " 접두어를 뺀 "홍길동"(제목이 앱 이름뿐이면 그대로). 다른 기기 알림은 거쳐 온 PC가 아니라 원래 기기("Galaxy S24 · 카카오톡")를 머리줄에 쓰고, 배지는 기기 이름에서 만든다(색은 이름 해시, 저장 안 함). 묶음 요약은 보조 텍스트에 보낸 곳, 제목에 앱. 같은 배지를 PC 목록 카드(이름 앞)와 알림 센터 카드 머리줄에도 그려 알림과 PC를 짝짓게 한다. 알림 센터의 다른 기기 알림 머리줄은 "Galaxy S24 · 카카오톡 · 작업실 경유". 목록에서 지운 PC의 지난 알림은 `pcId` 해시 색과 이름 첫 글자로 그린다. PC·프로토콜 변경 없음. | **제목 앞에 PC 이름 붙이기("[작업실] 홍길동")**: 가장 눈에 띄지만 제목이 길어져 정작 보낸 사람·내용이 잘리고, 머리줄과 PC 이름이 두 번 나온다 → 배지·색. **사용자가 PC 색을 직접 고르기**: 요구는 구분이지 꾸미기가 아니고 자동 색으로 충분하다(필드는 저장돼 있어 나중에 붙일 수 있음). **저장 없이 `pcId` 해시만으로 색**: 2대일 때도 1/8 확률로 같은 색이 된다. **목록 순서로 색 배정(저장 없음)**: 재페어링이 PC를 목록 끝으로 옮겨 색이 바뀐다. **대가**: 다른 기기 알림의 기기 색은 PC 색과 겹칠 수 있다(글자로 구분). Android가 앱 알림 4개 이상을 자동으로 묶은 접힌 줄("홍길동 오늘 회의…")에는 PC가 보이지 않는다 — 펼치면 배지가 보이고, 같은 PC·앱 묶음(M-37)은 접혀도 머리줄에 PC가 있다. |

## M (계속) — PC에서 연결된 기기 보기, 2026-11-16

요구(사용자): PC에서도 연결된 기기의 정보를, 휴대폰에서 PC 정보를 보는 수준으로 가능한 만큼 보고 싶다. 트레이 UI도 만족스럽지 않아 사용자 경험을 최대한 개선한다(트레이 중심, 콘솔 정체성 유지, 휴대폰 앱 화면은 바꾸지 않음).
확인한 사실: 트레이 대시보드는 460×640 고정 창에 이 PC의 CPU·RAM·디스크와 기기 한 줄 요약만 있었다(M-29에서 "트레이에서 다른 PC 실시간 보기"를 기각). PC↔PC 연결은 한쪽만 토큰을 가진다(B가 A에 연결하면 B만 A의 토큰을 가짐). 휴대폰은 배터리·충전·네트워크만 보고했다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-44 | **트레이 대시보드 = 기기 허브: 왼쪽 기기 목록, 오른쪽 고른 기기의 상세. PC는 실시간(휴대폰 앱 PC 화면 수준), 휴대폰은 늘린 마지막 보고 (M-29 일부 번복)** | **이 PC**: 트레이가 브리지 `live.get {target: self}`로 Agent의 `LiveSnapshotHub`를 *로컬* 구독자로 본다(로컬 구독은 "다른 기기가 보는 중" 표시를 켜지 않는다 — `watch`에 `remote` 추가). **이 PC가 연결한 PC(허브)**: Agent가 그 허브의 `/api/v1/live` WebSocket을 연결 때 받은 토큰과 같은 경로 규칙(LAN 인증서 고정·Tailscale·Cloudflare)으로 연다. **이 PC에 연결한 PC**: 이쪽엔 토큰이 없으므로 그 PC가 이미 하고 있는 inbox 롱 폴 응답에 `live: true`를 실어 공유를 요청하고, 그 PC가 2초마다 `POST /api/v1/live-snapshot`으로 보낸다(허브는 최신 하나만 메모리에). 트레이가 1초마다 `live.get`을 불러 보기를 연장하고, 10초(허브 쪽 요청은 15초) 동안 묻지 않으면 닫혀 수집도 멈춘다(M-5 유지). **휴대폰**: 서버가 없어 실시간은 불가 → presence에 표시용 필드(OS, 메모리, 저장공간, 배터리 온도·상태·충전 방식·완충까지, 절전, 발열, 신호, Wi-Fi 속도, 알림 전달, 방해 금지)를 더하고, 허브는 범위를 벗어난 필드만 버린다(보고 전체를 거절하지 않음). 앱은 1분 주기는 그대로 두고, 눈에 띄는 상태가 바뀔 때만 먼저 보낸다. 이 PC만 아는 것(푸시 등록·끈 종류·연결한 날)은 `devices.overview.local`로. 화면: 960×680 크기 조절 창(크기·고른 기기 기억), 휴대폰 앱의 밝은 콘솔 역할(옅은 바탕·흰 판·늘 어두운 카본 계기판), 링 게이지와 60초 추이, 앱·프로세스·작업 탭(5초마다만 순위 갱신), Segoe UI+맑은 고딕. 디자인 확인용 `Homebase.Tray.exe --preview-dashboard`(가짜 기기, 트레이 아이콘·Agent 없음). | **이쪽에서도 상대 PC 토큰을 받는 상호 페어링**: 보안 모델(누가 누구를 볼 권한)이 바뀌고, 연결한 쪽에서 반대쪽으로는 경로가 닿지 않을 수 있다(NAT·Quick Tunnel) → 이미 되는 방향(상대 → 이 PC)으로 보내게 함. **`GET /api/v1/snapshot`을 1초마다**: 요청마다 샘플러·ETW를 켰다 껐다 한다 → WebSocket. **휴대폰을 푸시로 깨워 즉시 보고**: ntfy 하루 한도를 쓰고 앱 화면·수신 경로가 바뀐다 → 마지막 보고와 보고 주기 안내. **트레이 다크 모드**: 모든 창의 StaticResource를 바꿔야 해 범위 밖. **대가**: 상대 PC가 v0.32.0 이하이면 이 PC에 연결한 쪽은 요약만 보인다(안내 표시). 공유는 보는 동안 2초마다 스냅숏(20~80KB)을 보낸다. 허브 쪽 요청이 시작돼도 상대 PC의 다음 inbox 응답까지(보통 즉시, 롱 폴이 다른 이유로 막혀 있으면 최대 25초) 기다린다. 다른 PC를 거쳐 보이는 기기는 마지막 보고만 보인다. 휴대폰 상태는 최대 1분(앱이 닫혀 있으면 약 30분) 늦을 수 있다. |

## M (계속) — 하드닝 묶음, 2026-11-16

배경: v0.13.0부터 릴리스마다 열어 둔 하드닝 후보 이슈(18개)와 버그 #2·#5를 한 번에 정리했다(v0.32.0). 동작이 바뀌는 것만 적는다.

| # | 결정 | 내용 | 기각한 대안 · 대가 |
|---|---|---|---|
| M-39 | **Cloudflare 터널로 온 익명 `/api/v1/info`는 PC 이름·OS를 비운다** | loopback 경로이면서(`cloudflared`가 여기로 넘긴다, M-17) Cloudflare가 켜져 있거나 `Cf-Ray`·`Cf-Connecting-Ip`·`Cdn-Loop` 헤더가 있으면 터널 요청으로 본다. 그때 Bearer 토큰이 페어링된 기기 것이 아니면 `pcName`·`os`를 `""`로 준다. 필드는 남긴다(앱이 `getString`으로 읽는다). 앱은 `/info`에도 기기 토큰을 실어 보내 터널 경로에서도 이름을 받고, 빈 이름은 무시한다. | **터널에서 `/info` 401**: 옛 앱이 터널 경로를 오프라인으로 본다. **헤더만으로 판정**: 로컬 프로세스도 헤더를 보낼 수 있지만, 판정은 응답을 줄이기만 하므로 괜찮다. **대가**: Cloudflare가 켜진 PC에서는 개발용 loopback 요청도 줄인 응답을 받는다. |
| M-40 | **경로 푸시(M-23)는 휴대폰 수신 시각도 본다** | 마지막으로 반영한 푸시보다 `ts`가 새것이거나, **휴대폰 시계로 10분 이상 지나** 도착한 것이면 받는다(서명 확인은 그대로). PC 시계가 뒤로 가면(RTC 불량 뒤 NTP) 그 뒤 모든 푸시가 옛것으로 보여 버려지던 문제를 막는다. 옛 앱이 저장한 항목은 수신 시각이 없어 다음 푸시 하나를 받는다. | **PC별 단조 카운터**: Agent 재설치·DB 초기화 때 되돌아간다. 대가: 10분 안에 늦게 온 중복은 여전히 `ts`로만 거르고, 10분 넘게 늦게 다시 배달된 옛 푸시는 받아 버린다. 배포자는 받은 순서대로 다시 배달하므로 뒤따르는 새 푸시가 곧 다시 덮는다. |
| M-41 | **일시 장애로 못 보낸 경보는 메모리에서 30분까지 다시 보낸다 (#2)** | `AlertDispatcher`가 Transient·RateLimited·EndpointOffline·Unknown 실패를 30초→1분→2분→5분→10분→10분 간격으로 최대 6번, 처음 실패 뒤 30분까지 다시 보낸다(대기 최대 50건, dedup_key당 하나). 다른 경보가 전달되면 대기 중인 것을 바로 보낸다. 성공하면 `alert_log` 행을 `Sent`로 바꾼다. 같은 작업에서 `ExistsRecentDedupKeyAsync`가 문자열로 날짜를 비교해 dedup 창이 사실상 "UTC 자정까지"였던 것을 `julianday()` 비교로 고쳤다. | **`alert_log`의 `Failed` 행을 디스크에서 다시 보내기**: 재시작 뒤에도 남지만 오래된 경보가 뒤늦게 쏟아질 수 있다. 대가: Agent가 재시작하면 대기 중인 재전송은 사라지고, 재전송은 mute·분당 제한을 다시 거치지 않는다. dedup 수정으로 같은 키의 경보가 설정한 창(기본 10분)마다 다시 나간다. |
| M-42 | **PC끼리의 inbox 롱 폴은 동시에 16개까지** | 넘으면 새 알림이 없을 때 즉시 503 `busy`로 답한다. 받는 쪽(`HubLinkService`)은 2xx가 아니면 30초 뒤 같은 커서로 다시 묻고 inbox는 30분 보관하므로 잃지 않는다. 휴대폰 요청이 API의 동시 연결 32개를 못 얻는 일을 막는다. | **빈 200 즉시 응답**: 받는 쪽이 쉬지 않고 다시 요청한다. 대가: 17번째 PC부터는 알림이 최대 약 30초 늦다. |
| M-43 | **Tray 파이프는 SYSTEM·Administrators·설치 사용자(없으면 INTERACTIVE)만 (#5)** | `Homebase.NotificationPipe`도 `Homebase.MobileBridge`와 같은 ACL(`TrayPipeSecurity`)을 쓴다. 서버 자신에게는 `CreateNewInstance`를 포함한 전체 권한을 준다. Agent가 모르는 브리지 연산은 연결을 끊지 않고 `{ok:false, error:"unsupported"}`로 답한다. | 대가: `mobile.userSid`가 설정돼 있으면 다른 Windows 사용자의 Tray는 토스트 포워딩에도 붙지 못한다(의도). v0.30.0 이하 Agent는 여전히 모르는 연산에서 연결을 끊는다. |
