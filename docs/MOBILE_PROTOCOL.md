# Homebase Mobile Protocol v1

휴대폰 앱(`mobile/`)과 각 PC의 Agent 사이 계약이다. 결정 배경은 [`DECISIONS.md`](../DECISIONS.md) M-1~M-18에 있다.
형식은 [`mobile-protocol/`](mobile-protocol/)의 픽스처로 고정한다. .NET(`ProtocolGoldenTests`)과 Kotlin(`ProtocolTest`)이 같은 파일을 검증한다.
의도적으로 스키마를 바꿀 때는 `HOMEBASE_UPDATE_GOLDEN=1 dotnet test`로 픽스처를 다시 쓰고, 호환이 깨지면 `schema`를 올린다.

## 연결

HTTP/1.1 + WebSocket, 요청당 연결 하나(`Connection: close`). 경로(`transport`)는 셋이고 인증은 모두 같은 Bearer 토큰이다.

| transport | 주소 | 암호화 | Agent 바인딩 · 허용 상대 | 설정 |
|---|---|---|---|---|
| `lan` | PC의 사설 IPv4(기본 게이트웨이가 있는 어댑터만) : `mobile.port` | TLS 1.2/1.3, 자체 서명 인증서. 앱은 `certSha256`(DER의 SHA-256)과 일치하는 인증서만 받는다 | 그 LAN IP · 같은 서브넷의 사설 주소 | `mobile.lan` |
| `tailscale` | MagicDNS 이름, 100.x IP : `mobile.port` | 평문(WireGuard가 암호화) | tailnet IP · 100.64.0.0/10과 루프백 | `mobile.tailscale`(기본 켜짐) |
| `cloudflare` | 터널 호스트명 : 443 | Cloudflare 엣지의 공인 TLS | 127.0.0.1 · 루프백(cloudflared가 연결) | `mobile.cloudflare.*` |

- 경로 선택(앱, M-18): PC 서브넷 안의 LAN → (휴대폰 Tailscale VPN이 켜져 있으면) Tailscale → Cloudflare → 나머지 순으로 모두 시도한다.
  같은 순위 안에서는 마지막으로 성공한 경로를 먼저 쓴다. 네트워크 정보를 못 읽으면 LAN → Tailscale → Cloudflare.
- 경로 갱신: 앱은 (재)연결할 때마다 `GET /api/v1/endpoints`로 현재 경로를 받아 저장한다(DHCP 변경, Quick Tunnel 새 주소).
- `loopback`(127.0.0.1, `mobile.bindLoopback`)은 개발·에뮬레이터(adb reverse) 전용이다.

## 페어링 QR

QR 텍스트는 링크다(M-19, [`pairing-link.v1.txt`](mobile-protocol/pairing-link.v1.txt)).

```
https://jeiel85.github.io/homebase/pair/#<base64url(UTF-8 JSON {hb, pcId, pcName, code, expiresAt, endpoints})>
```

- 앱이 있으면 링크가 앱의 페어링 화면을 연다(App Link). 앱이 없으면 [`docs/pair/`](pair/index.html) 설치 안내 페이지가 열린다.
  그 페이지의 **앱에서 열기**는 같은 payload를 `homebase://pair/<payload>`로 앱에 넘긴다.
- payload는 fragment에만 있어 서버로 가지 않는다. 링크로 열린 경우 앱은 내용을 채워 두고 사용자가 **연결**을 눌러야 페어링한다.
- 앱은 아래의 이전 JSON 원문 QR도 계속 읽는다.

이전(그리고 붙여넣기용) JSON 형식([`pairing-offer.v1.json`](mobile-protocol/pairing-offer.v1.json)):

```json
{ "hb": 1, "pcId": "…", "pcName": "HOME-PC", "hosts": ["home-pc.tailXXXX.ts.net", "100.x.y.z"],
  "port": 47820, "code": "<128bit base64url>", "expiresAt": "2026-11-13T09:35:00+00:00",
  "endpoints": [
    { "transport": "lan", "host": "192.168.0.10", "port": 47820, "tls": true, "certSha256": "<64 hex>" },
    { "transport": "tailscale", "host": "home-pc.tailXXXX.ts.net", "port": 47820, "tls": false },
    { "transport": "cloudflare", "host": "home-pc.example.com", "port": 443, "tls": true } ] }
```

`endpoints`는 추가 필드라 `hb`·`schema`는 1 그대로다. `hosts`+`port`는 이전 앱용 Tailscale 목록으로 계속 채운다(TLS가 필요한 LAN은 넣지 않는다).
`endpoints`가 없는 이전 PC의 QR은 앱이 `hosts`를 Tailscale 경로로 읽는다. `certSha256`이 없거나 형식이 틀린 `lan` 항목은 버린다.

## REST

| 메서드 | 경로 | 인증 | 응답 |
|---|---|---|---|
| GET | `/api/v1/info` | 없음 | `{schema, protocol, pcId, pcName, agentVersion, os}` |
| POST | `/api/v1/pair` | 본문의 `code` | 200 `{schema, deviceId, token, pcId, pcName}` · 400 `invalid_code` · 410 `code_expired` · 429 `locked` |
| GET | `/api/v1/endpoints` | `Authorization: Bearer <token>` | 200 `{schema, endpoints}`([`endpoints.v1.json`](mobile-protocol/endpoints.v1.json)) · 401 |
| GET | `/api/v1/snapshot` | `Authorization: Bearer <token>` | 200 `LiveSnapshot` · 401 `unauthorized` |

`POST /api/v1/pair` 본문은 `{ "code", "deviceName", "deviceModel" }`이다. 오류 응답은 항상 `{schema, error, message}` 형태다.

## Live (WebSocket)

`GET /api/v1/live`에 `Authorization: Bearer <token>`을 붙여 업그레이드한다. 401은 업그레이드 전에 일반 HTTP 응답으로 돌아온다.
서버는 `mobile.liveIntervalMs`(기본 1000ms) 주기로 `LiveSnapshot` 텍스트 프레임을 보낸다. 느린 클라이언트에는 쌓이지 않고 최신 것만 간다.
클라이언트가 닫으면 마지막 구독자일 때 PC는 수집을 멈춘다.
서버는 매 전송 전에 기기의 페어링 상태를 다시 확인한다. Tray에서 철회된 기기는 다음 주기 안에 close `1008 "revoked"`로 끊기고, 앱은 이를 401과 같게 처리한다.

`LiveSnapshot`([`snapshot.v1.json`](mobile-protocol/snapshot.v1.json)):

| 필드 | 의미 |
|---|---|
| `system` | CPU %, RAM 사용/전체, I/O·네트워크 bytes/s, 가동 초 |
| `sessionAvailable` | Tray가 최근 15초 안에 창 목록을 보냈는가. false면 `apps`는 비고 앱이 "사용자 세션 없음"을 보인다 |
| `netPerProcess` | ETW 세션이 도는가. false면 프로세스별 `netRxBps/netTxBps` 필드가 빠진다(→ 앱은 "—") |
| `apps[]` | 창을 가진 프로세스 하나당 항목 하나. `windows[]`(제목, 전경 여부)와, 자기 창이 없는 자손 프로세스까지 합친 `usage`, 상위 20개 `children[]` |
| `processes[]` | CPU 상위 30개 프로세스(창 없는 백그라운드 에이전트용) |
| `claudeSessions[]` | 최근 10분 안에 활동한 Claude Code 세션의 제목, 작업 폴더, 마지막 활동 시각 |

집계 규칙(`AppAggregator`):
- 부모 PID가 재사용된 경우(부모가 자식보다 늦게 생성됨)는 부모로 보지 않는다.
- 자기 창이 있는 자손은 별도 앱이다.
- `explorer`는 하위 트리를 합치지 않는다.

## Tray ↔ Agent (`\\.\pipe\Homebase.MobileBridge`)

프레임은 4바이트 LE 길이 + UTF-8 JSON `{op, id?, payload?}`이고 최대 1MiB다.
- Agent → Tray: `watch {on}`, `response {id, ok, payload?, error?}`.
- Tray → Agent: `session.report`(watch on일 때 2초마다), `pairing.begin`, `devices.list`, `devices.revoke {deviceId}`, `status`.
- 그 밖의 op는 연결을 끊는다.
