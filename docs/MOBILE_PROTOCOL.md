# Homebase Mobile Protocol v1

휴대폰 앱(`mobile/`)과 각 PC의 Agent 사이 계약이다. 결정 배경은 [`DECISIONS.md`](../DECISIONS.md) M-1~M-14에 있다.
형식은 [`mobile-protocol/`](mobile-protocol/)의 픽스처로 고정한다. .NET(`ProtocolGoldenTests`)과 Kotlin(`ProtocolTest`)이 같은 파일을 검증한다.
의도적으로 스키마를 바꿀 때는 `HOMEBASE_UPDATE_GOLDEN=1 dotnet test`로 픽스처를 다시 쓰고, 호환이 깨지면 `schema`를 올린다.

## 연결

- 전송: 평문 HTTP/1.1 + WebSocket. 경로는 Tailscale(WireGuard)이 암호화한다. 요청당 연결 하나(`Connection: close`).
- 주소: QR의 `hosts`를 순서대로 시도한다(MagicDNS 이름 → 고정 Tailscale IP). 앱은 마지막으로 성공한 host를 먼저 쓴다.
- 포트: 기본 47820(`mobile.port`).
- Agent는 tailnet(100.64.0.0/10)과 루프백 외의 상대를 연결 즉시 끊는다.

## 페어링 QR

QR 텍스트는 JSON이다([`pairing-offer.v1.json`](mobile-protocol/pairing-offer.v1.json)).

```json
{ "hb": 1, "pcId": "…", "pcName": "HOME-PC", "hosts": ["home-pc.tailXXXX.ts.net", "100.x.y.z"],
  "port": 47820, "code": "<128bit base64url>", "expiresAt": "2026-11-13T09:35:00+00:00" }
```

## REST

| 메서드 | 경로 | 인증 | 응답 |
|---|---|---|---|
| GET | `/api/v1/info` | 없음 | `{schema, protocol, pcId, pcName, agentVersion, os}` |
| POST | `/api/v1/pair` | 본문의 `code` | 200 `{schema, deviceId, token, pcId, pcName}` · 400 `invalid_code` · 410 `code_expired` · 429 `locked` |
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
