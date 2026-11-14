# Homebase Mobile Protocol v1

휴대폰 앱(`mobile/`)과 각 PC의 Agent 사이 계약이다. 결정 배경은 [`DECISIONS.md`](../DECISIONS.md) M-1~M-21에 있다.
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

QR 텍스트는 링크다(M-20, [`pairing-link.v1.txt`](mobile-protocol/pairing-link.v1.txt)).

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
| POST | `/api/v1/push` | `Authorization: Bearer <token>` | 200 `{schema, deviceId, registered: true}` · 400 `invalid_subscription` · 401 |
| DELETE | `/api/v1/push` | `Authorization: Bearer <token>` | 200 `{schema, deviceId, registered: false}` · 401 |
| POST | `/api/v1/presence` | `Authorization: Bearer <token>` | 200 `DevicesResponse` · 400 `invalid_presence` · 401 (M-27) |
| GET | `/api/v1/devices` | `Authorization: Bearer <token>` | 200 `DevicesResponse` · 401 (M-27) |
| POST | `/api/v1/notifications` | `Authorization: Bearer <token>` | 202 `{schema, accepted: true}` · 400 `invalid_notification` · 401 · 503 `forwarding_off` (M-28) |
| GET | `/api/v1/inbox?after=<seq>` | `Authorization: Bearer <token>` | 200 `{schema, next, items[]}` — 새 항목이 없으면 최대 25초 기다린다 · 401 (M-28) |
| DELETE | `/api/v1/device` | `Authorization: Bearer <token>` | 200 `{schema, deviceId, removed}` — 기기가 스스로 페어링을 끊는다 · 401 (M-29) |

`POST /api/v1/pair` 본문은 `{ "code", "deviceName", "deviceModel", "kind"? }`이다. `kind`는 `phone`(기본, 옛 앱은 보내지 않음) 또는 `pc`(다른 PC가 이 PC에 붙을 때, M-29)다. 오류 응답은 항상 `{schema, error, message}` 형태다.

### 기기 공유 (M-27 ~ M-29)

PC는 페어링한 기기(휴대폰·다른 PC)의 허브다.
- **상태**: 기기는 `POST /api/v1/presence`로 자기 상태를 보낸다([`presence.v1.json`](mobile-protocol/presence.v1.json)): `{schema, kind, appVersion?, battery?, charging?, network?, cpuPct?, memUsedBytes?, memTotalBytes?, uptimeSec?, pushHashes?}`. 휴대폰은 배터리·네트워크를, PC는 CPU·메모리·가동 시간을 채운다. 응답과 `GET /api/v1/devices`는 [`devices.v1.json`](mobile-protocol/devices.v1.json): `{schema, hub: {pcId, pcName, agentVersion, status}, devices: [{deviceId, name, kind, model?, lastSeenAt?, online, status?}], pushHashes?}`. 상태는 PC 메모리에만 있고 2분 넘게 보고가 없으면 `online=false`. 휴대폰은 앱을 열거나 목록이 PC를 확인할 때, 그리고 30분 주기 잡에서, PC는 30초마다 보고한다.
- **`pushHashes`**: UnifiedPush 엔드포인트 URL의 SHA-256(소문자 hex). PC가 자기 휴대폰 목록을 보내고(presence), 허브는 자기 휴대폰 목록을 돌려준다(devices). 같은 휴대폰이 두 PC에 붙어 있으면 중계 알림을 한 번만 받도록 보내는 쪽에서 건너뛴다.
- **알림 중계**: `POST /api/v1/notifications` 본문은 [`device-notification.v1.json`](mobile-protocol/device-notification.v1.json): `{schema, id, app (≤64), title (≤200), body? (≤1000), ts}`. 허브는 마스킹한 뒤 자기 트레이에 띄우고, 범주 **`device-notification`**으로 다른 휴대폰에 푸시한다(보낸 기기와 그 기기의 `pushHashes`는 제외). 푸시 제목은 `"<기기> · <앱>: <제목>"`이다. 푸시 `id`는 원본 알림 `id`에서 만들어 모든 PC에서 같으므로, 두 PC에 함께 붙은 휴대폰은 같은 중계 알림을 한 번만 띄운다(앱이 30분 안의 같은 `id`를 버림). 허브의 Windows 알림 forwarding이 꺼져 있으면 503 `forwarding_off`.
- **inbox**: 연결된 PC가 롱 폴링한다. `{schema, next, items: [{seq, id, originId, originName, app, title, body?, ts}]}` — 허브의 Windows 알림(`originId: "self"`)과 기기에서 받은 알림의 최근 200건·30분, 요청한 기기가 보낸 것은 뺀다. `after`가 허브의 `next`보다 크면(허브 재시작) 처음부터 준다. 받은 PC는 이 항목을 다시 중계하지 않는다(한 단계). `id`는 모든 경로에서 같아서, 두 PC가 서로 연결돼 같은 알림이 두 길로 와도 한 번만 띄운다.

## 푸시 알림 (UnifiedPush, M-19 · M-21)

앱이 UnifiedPush 배포자(ntfy 등)에서 받은 엔드포인트와 RFC 8291 구독 키를 페어링된 각 PC에 등록한다.
PC는 알림마다 그 엔드포인트로 암호문을 POST하고, 배포자가 앱에 브로드캐스트로 넘긴다. 중계 서버는 평문을 보지 않는다.

- **등록** `POST /api/v1/push` 본문([`push-subscription.v1.json`](mobile-protocol/push-subscription.v1.json)):
  `{schema: 1, endpoint, p256dh, auth, mutedCategories?}`. `endpoint`는 `https://` URL, 1000바이트 이하이며 사용자 정보(`user:pw@`)는 거부한다.
  `mutedCategories`(M-25)는 이 휴대폰이 받지 않을 `category` 목록이다(최대 32개, 각 `a-z0-9_-` 1~64자, 아니면 400). 없거나 비면 전부 받는다.
  PC는 정렬·중복 제거해 저장하고, 이 목록만 바뀐 재등록도 갱신으로 본다. 해당 범주는 그 휴대폰에 보내지 않으며(`test`는 예외),
  모든 휴대폰이 끈 범주는 전송 성공으로 처리해 Telegram으로 넘기지 않는다.
  `p256dh`는 P-256 비압축 점 65바이트, `auth`는 16바이트이고 둘 다 패딩 없는 base64url이다. 곡선 위의 점이 아니면 400이다.
  응답은 [`push-subscription-response.v1.json`](mobile-protocol/push-subscription-response.v1.json).
  구독은 기기당 하나이며 `state.json`의 그 기기 항목에 저장한다. 같은 값으로 다시 등록하면 파일을 다시 쓰지 않는다.
  같은 엔드포인트를 가진 다른 기기는 같은 휴대폰의 예전 페어링이므로 철회한다(M-24). Agent 시작 때도 중복을 정리하고 가장 최근 페어링만 남긴다.
  Tray에서 기기를 철회하면 구독도 함께 지워진다. 앱은 엔드포인트가 바뀔 때, 페어링 직후, PC가 응답할 때(목록 확인·실시간 연결)마다 다시 등록한다.
- **해제** `DELETE /api/v1/push`: 앱에서 푸시를 끄거나 PC를 목록에서 지울 때 보낸다.
- **전송**(PC → 엔드포인트): `Content-Encoding: aes128gcm`, `Content-Type: application/octet-stream`, `TTL`(Windows 알림 21600초, 그 밖 86400초),
  `Urgency`(Critical·Warning은 `high`, 나머지 `normal`). 본문은 RFC 8291(ECDH P-256 + HKDF-SHA-256 + AES-128-GCM, 레코드 하나, rs 4096)이고
  4096바이트 이하다. 평문 JSON이 3993바이트를 넘으면 본문을, 그래도 넘으면 제목을 줄인다(끝에 `…`). VAPID는 보내지 않는다(ntfy는 요구하지 않음).
  응답 처리: 2xx 성공, 429는 `Retry-After`(없으면 60초, 최대 1시간) 동안 그 엔드포인트를 쉬게 하고, 404·410은 구독을 지운다. 그 밖의 실패는 다음 채널(Telegram)로 넘어간다.
- **복호화된 메시지**([`push-message.v1.json`](mobile-protocol/push-message.v1.json)):

| 필드 | 의미 |
|---|---|
| `schema` | 1. 다르면 앱은 버린다 |
| `id` | 알림 id(32 hex) |
| `pcId`, `pcName` | 보낸 PC. 앱은 페어링된 PC가 아니면 버린다 |
| `category` | `windows-toast`, `process_watch`, `service_watch`, `http_monitor`, `port_monitor`, `event_log`, `advisor`, `boot`, `test` |
| `priority` | `info`·`warning`·`critical`·`recovery` |
| `title`, `body?` | 표시할 텍스트(평문, HTML 아님) |
| `source` | 알림 채널 키. Windows 알림은 원래 앱 이름, 그 밖은 `category`와 같다 |
| `count` | 묶음 건수(앱별 묶음이면 2 이상) |
| `ts` | 생성 시각(ISO 8601) |

- 앱의 Android 알림 채널은 PC(채널 그룹)마다, `source`마다 하나씩이다. 사용자가 PC·앱 단위로 끌 수 있다.
- 두 구현이 맞는지는 [`push-message.v1.aes128gcm.txt`](mobile-protocol/push-message.v1.aes128gcm.txt)로 확인한다.
  .NET이 RFC 8291 부록 A의 서버 키·salt로 위 메시지를 암호화해 쓰고, Kotlin 테스트가 부록 A의 휴대폰 키로 복호화해 같은 메시지인지 본다.

### 경로 알림 (M-23)

PC는 새 경로(재시작 후 바뀐 Quick Tunnel 주소, 새 LAN IP 등)가 생기면 같은 방식으로 암호화한 **경로 알림**을 보낸다.
앱은 경로를 연결에 성공한 뒤에만 `/api/v1/endpoints`로 받으므로, 이것이 없으면 옛 주소만 아는 앱은 다시 페어링해야 한다.

- 평문([`push-endpoints.v1.json`](mobile-protocol/push-endpoints.v1.json)): `{schema: 1, kind: "endpoints", pcId, data, mac}`.
  `data`는 UTF-8 JSON `{schema: 1, pcId, ts, endpoints}`의 base64url이고, `endpoints`는 `/api/v1/endpoints`와 같은 형식이다.
  `mac`은 `data` 바이트의 HMAC-SHA256(base64url)이며, 키는 이 기기 Bearer 토큰의 SHA-256 원시 32바이트다. 픽스처는 토큰 `fixture-token`으로 서명했다.
- 앱은 `kind`가 `endpoints`이면 알림을 띄우지 않고, 페어링된 PC의 토큰으로 MAC을 확인하고 안팎 `pcId`가 같은지 본 뒤 경로를 바꾼다.
  마지막으로 반영한 경로 알림의 `ts`보다 새것만 받는다. 휴대폰 푸시 키는 모든 PC가 공유하므로, MAC이 없으면 한 PC가 다른 PC의 경로를 바꿀 수 있다.
- PC는 경로 목록이 15초 간격으로 두 번 같고 이전에 알리지 않은 경로가 있을 때만 보낸다. 경로가 빠지기만 하면 보내지 않는다. `TTL` 86400초, `Urgency: normal`. 휴대폰마다 따로 기억하며, 받지 못한 휴대폰에만 5분 뒤 다시 보낸다.
- 이전 앱은 `id`·`title`이 없으므로 이 메시지를 버린다.

### 앱 쪽 UnifiedPush 규약 (AND_3.1.0, 라이브러리 없이)

- 배포자 찾기: `queryBroadcastReceivers(org.unifiedpush.android.distributor.REGISTER)`(매니페스트 `<queries>`). 하나면 자동으로 쓰고, 여럿이면 고르게 하고, 없으면 ntfy 설치를 안내한다.
- 등록: `REGISTER`에 `token`(UUID, 등록마다 새로), `message`, `pi`(`org.unifiedpush.dummy_app`을 향한 IMMUTABLE PendingIntent), 구버전 호환용 `application`을 넣는다. API 34+는 `BroadcastOptions.setShareIdentityEnabled(true)`로 보낸다.
- 수신(`PushReceiver`, exported): `NEW_ENDPOINT`, `MESSAGE`(`bytesMessage`), `REGISTRATION_FAILED`, `UNREGISTERED`, `TEMP_UNAVAILABLE`. 현재 `token`이 아니면 무시하고, `id`가 있으면 `MESSAGE_ACK`로 답한다.
  `MESSAGE`는 이 휴대폰 키·auth로 복호화되어야(GCM 태그) 표시되므로, 다른 앱이 보낸 가짜 브로드캐스트는 아무것도 띄우지 못한다. 송신 측 공개키는 곡선 위의 점인지 먼저 검사한다.
- `RAISE_TO_FOREGROUND` 서비스는 바인드만 받는다(규약 요구).
- 키: P-256 키쌍과 auth secret은 설치마다 하나이고 모든 PC가 같이 쓴다. 개인키(PKCS#8)와 auth는 Android Keystore AES-GCM 키로 감싸 SharedPreferences에 둔다.

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
| `claudeSessions[]` | 최근 10분 안에 활동한 Claude Code 세션의 제목, 작업 폴더, 마지막 활동 시각(옛 앱용, `work`에도 들어 있다) |
| `work[]` | 작업 탭(M-26): `{kind, app, title, detail?, lastActivity?, foreground}`. `kind`는 `agent`(Claude Code·Codex 세션, `detail`=폴더) · `editor`(코드 편집기, `detail`=프로젝트) · `document`(Office·한글·PDF 등) · `browser`(브라우저 창의 활성 탭). 순서는 agent(최근 활동순) → 전경 창 → editor → document → browser, 최대 30개. v0.23.0 전 PC는 보내지 않는다 |

집계 규칙(`AppAggregator`):
- 부모 PID가 재사용된 경우(부모가 자식보다 늦게 생성됨)는 부모로 보지 않는다.
- 자기 창이 있는 자손은 별도 앱이다.
- `explorer`는 하위 트리를 합치지 않는다.

## Tray ↔ Agent (`\\.\pipe\Homebase.MobileBridge`)

프레임은 4바이트 LE 길이 + UTF-8 JSON `{op, id?, payload?}`이고 최대 1MiB다.
- Agent → Tray: `watch {on}`, `toast.show {title, body?}`(다른 기기 알림을 이 PC에 띄움, M-28), `response {id, ok, payload?, error?}`.
- Tray → Agent: `session.report`(watch on일 때 2초마다), `pairing.begin`, `devices.list`, `devices.revoke {deviceId}`, `status`,
  `notify.test`(Tray의 테스트 메시지를 Agent의 router로 보냄 → `{delivered, channel?, error?}`),
  `devices.overview`(→ `{here: DevicesResponse, hubs: [{pcId, pcName, online, lastContactAt?, status?, devices[], error?, selfDeviceId?}]}`),
  `hubs.link {link}`(다른 PC의 페어링 링크로 연결 → `{linked, pcName?, error?, message?}`), `hubs.unlink {pcId}`(M-29).
  `session.report`에는 `agents[]`(Codex 세션 등 Claude 밖의 작업, M-26)가 더해졌다.
- 그 밖의 op는 연결을 끊는다.
