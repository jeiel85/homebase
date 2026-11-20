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
| GET | `/api/v1/info` | 없음 | `{schema, protocol, pcId, pcName, agentVersion, os}`. Cloudflare 터널로 온 요청은 페어링된 기기의 `Authorization: Bearer <token>`이 없으면 `pcName`·`os`가 `""`다(M-39) |
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
| POST | `/api/v1/approval` | `Authorization: Bearer <token>` | 본문 `{schema, id, decision}`(`allow`·`deny`) → 200 `{schema, accepted}`. Claude Code 권한 요청(M-48, 푸시의 `approval.id`)에 답한다. 410 `approval_gone`: 이미 답했거나 PC가 더는 기다리지 않음(다른 휴대폰이 먼저 답함, 사용자가 PC로 돌아옴, 10분 지남), 400 잘못된 본문 |
| POST | `/api/v1/advise` | `Authorization: Bearer <token>` | 본문 `{schema}` → 200 [`advice.v1.json`](mobile-protocol/advice.v1.json) `{schema, ok, text?, summary?, model?, generatedAt, cached, error?, message?}` · 401 (M-60). PC 의 로컬 AI(Ollama) 조언. 조언을 못 만든 것도 200 + `ok: false`: `error` 는 `disabled`·`unreachable`·`model_missing`·`timeout`·`server_error`·`empty`·`failed`, `message` 는 영어 설명. 생성은 한 번에 하나, 성공한 조언은 60초 동안 다시 줌(`cached: true`). 최대 `llmAdvisor.timeoutSeconds`(기본 60초) 걸리므로 읽기 대기를 길게(앱 90초). `text` 는 일반 텍스트로만 보인다. v0.45.0 이하 PC 는 404 |
| POST | `/api/v1/live-snapshot` | `Authorization: Bearer <token>` (PC만) | 200 `{schema, live}` · 400 `invalid_snapshot` · 401 · 403 (M-46) |
| POST | `/api/v1/clip/key` | `Authorization: Bearer <token>` | 본문 `{schema, pub}`(기기의 새 P-256 공개키) → 200 `{schema, pub}`(PC의 새 공개키) · 400 `invalid_key` · 401 — 클립보드 키 합의 (M-62, [`clip-key.v1.json`](mobile-protocol/clip-key.v1.json)). v0.48.0 이하 PC 는 404 |
| POST | `/api/v1/clip` | `Authorization: Bearer <token>` (토큰 확인 뒤 본문 최대 1MiB) | 본문 [`clip.v1.json`](mobile-protocol/clip.v1.json) → 200 `{schema, accepted: true}` · 400 `invalid_clip` · 401 · 409 `clip_key_needed`·`no_user_session` · 503 `clipboard_busy` (M-62, 아래 "클립보드"). v0.48.0 이하 PC 는 404 |
| POST | `/api/v1/transfer` | `Authorization: Bearer <token>` (토큰 확인 뒤 256KiB) | 파일 보내기 시작(M-64, 아래 "파일"): 봉투(전송 id, 메타데이터) → 200 `{schema, id, state, chunkBytes, have}` · 400 `invalid_transfer` · 409 `clip_key_needed` · 413 `too_large` · 503 `busy` · 507 `insufficient_storage`. v0.49.0 이하 PC 는 404 |
| POST | `/api/v1/transfer/chunk?id=&f=&n=` | `Authorization: Bearer <token>` (토큰 확인 뒤 4MiB + 16) | 봉인한 조각 → 200 · 400 `bad_chunk` · 409 `unknown_transfer` · 503 `busy` |
| GET · DELETE | `/api/v1/transfer?id=` | `Authorization: Bearer <token>` | 상태(받은 조각) · 취소. 409 `unknown_transfer` |
| POST | `/api/v1/transfer/done?id=` | `Authorization: Bearer <token>` | 봉투(파일별 SHA-256) → 200 `{schema, saved}`(`tray`·`waiting`) · 409 `incomplete`·`hash_mismatch`·`unknown_transfer` |
| GET | `/api/v1/transfer/offer?id=` | `Authorization: Bearer <token>` | PC가 그 휴대폰에 보내는 파일(M-65, 아래 "PC가 보내는 파일"): 봉투(이름·크기·MIME·SHA-256) · 409 `unknown_transfer` |
| GET | `/api/v1/transfer/fetch?id=&f=&n=` | `Authorization: Bearer <token>` | 그 휴대폰 키로 봉인한 조각, `application/octet-stream` · 400 `bad_chunk` · 409 `unknown_transfer` |
| POST | `/api/v1/transfer/received?id=` | `Authorization: Bearer <token>` | 본문 `{schema}` → 200: 휴대폰이 모든 파일을 받았다 |
| GET | `/api/v1/clip?id=<id>` | `Authorization: Bearer <token>` | 200 봉투(그 기기 키로 봉인) · 404 `clip_gone` — PC가 그 기기에 알린 긴 텍스트, 10분 (M-62) |

`POST /api/v1/pair` 본문은 `{ "code", "deviceName", "deviceModel", "kind"? }`이다. `kind`는 `phone`(기본, 옛 앱은 보내지 않음) 또는 `pc`(다른 PC가 이 PC에 붙을 때, M-29)다. 오류 응답은 항상 `{schema, error, message}` 형태다.

### 기기 공유 (M-27 ~ M-29)

PC는 페어링한 기기(휴대폰·다른 PC)의 허브다.
- **상태**: 기기는 `POST /api/v1/presence`로 자기 상태를 보낸다([`presence.v1.json`](mobile-protocol/presence.v1.json)): `{schema, kind, appVersion?, battery?, charging?, network?, cpuPct?, memUsedBytes?, memTotalBytes?, uptimeSec?, pushHashes?}`. 휴대폰은 배터리·네트워크를, PC는 CPU·메모리·가동 시간을 채운다.
  v0.34.0(M-46)부터 표시용 필드가 더해졌다: `os`(≤64자), `storageUsedBytes`·`storageTotalBytes`, `batteryTempC`(−40~120), `batteryHealth`(`good`·`overheat`·`dead`·`over_voltage`·`cold`·`failure`), `plug`(`ac`·`usb`·`wireless`·`dock`), `chargeFullSec`(완충까지 초, 0~7일), `powerSave`, `thermal`(`none`~`shutdown`), `signal`(0~4칸), `linkMbps`(Wi-Fi 링크 속도), `forwarding`(이 휴대폰이 자기 알림을 PC로 보내는 중), `dnd`(방해 금지). 휴대폰은 메모리·가동 시간도 채우고, PC는 `os`를 채운다. 범위를 벗어난 표시용 필드는 허브가 버리며 보고 전체를 거절하지 않는다. 앱은 배터리·충전·네트워크·충전 방식·절전·발열·알림 전달·방해 금지·신호가 바뀔 때만 1분 주기보다 먼저 다시 보낸다. 응답과 `GET /api/v1/devices`는 [`devices.v1.json`](mobile-protocol/devices.v1.json): `{schema, hub: {pcId, pcName, agentVersion, status}, devices: [{deviceId, name, kind, model?, lastSeenAt?, online, status?}], pushHashes?}`. 상태는 PC 메모리에만 있고 2분 넘게 보고가 없으면 `online=false`. 휴대폰은 앱을 열거나 목록이 PC를 확인할 때, 그리고 30분 주기 잡에서, PC는 30초마다 보고한다.
- **`endpoints`** (v0.45.0, M-59, PC만): presence를 보내는 PC가 지금 자기 경로 목록(`GET /api/v1/endpoints`와 같은 모양, 루프백 제외, 최대 16개)을 싣는다. 받은 PC가 보낸 PC(`pcId`)에 거는 자기 연결이 지금 닿지 않을 때만 쓴다: 쓸 수 있는 것만 골라(LAN = 사설 IPv4 + TLS + 인증서 핀, Tailscale = tailnet 주소 또는 `*.ts.net` 이름 + 평문 HTTP, Cloudflare = 호스트 이름 + TLS, 최대 8개) 저장한 경로를 바꾸고 바로 다시 시도한다. 그 연결이 닿고 있으면 무시한다(10분마다 스스로 읽음). 휴대폰이 보내면 버린다. 이전 허브는 이 필드를 모른 채 무시한다.
  경로를 새로 읽을 때(10분마다·위 경우) 새 목록에 Tailscale 경로가 하나도 없으면 전에 알던 Tailscale 경로는 남긴다(tailnet 주소·이름은 바뀌지 않으므로, 상대 PC의 Tailscale이 잠깐 꺼진 때 읽은 목록으로 잃지 않게).
- **`pushHashes`**: UnifiedPush 엔드포인트 URL의 SHA-256(소문자 hex). PC가 자기 휴대폰 목록을 보내고(presence), 허브는 자기 휴대폰 목록을 돌려준다(devices). 같은 휴대폰이 두 PC에 붙어 있으면 중계 알림을 한 번만 받도록 보내는 쪽에서 건너뛴다.
- **알림 중계**: `POST /api/v1/notifications` 본문은 [`device-notification.v1.json`](mobile-protocol/device-notification.v1.json): `{schema, id, app (≤64), title (≤200), body? (≤1000), ts}`. 허브는 마스킹한 뒤 자기 트레이에 띄우고, 범주 **`device-notification`**으로 다른 휴대폰에 푸시한다(보낸 기기와 그 기기의 `pushHashes`는 제외). 푸시 제목은 `"<기기> · <앱>: <제목>"`이다. 푸시 `id`는 원본 알림 `id`에서 만들어 모든 PC에서 같으므로, 두 PC에 함께 붙은 휴대폰은 같은 중계 알림을 한 번만 띄운다(앱이 30분 안의 같은 `id`를 버림). 허브의 Windows 알림 forwarding이 꺼져 있으면 503 `forwarding_off`.
- **PC 연결 코드 (M-30)**: 트레이의 "코드 복사"는 지금 오퍼를 24단어(BIP39 영어, 256비트 + 8비트 체크섬)로 준다. 32바이트 = 버전 1 · SHA-256(`pcId`) 앞 4바이트 · LAN 인증서 SHA-256 앞 8바이트(LAN 없으면 0) · 포트(빅엔디언 2바이트) · 페어링 `code`의 16바이트 · 예약 0. 받는 PC는 온라인 tailnet 피어와 자기 LAN /24 호스트에 `GET /api/v1/info`를 묻고(LAN은 지문 앞부분이 맞는 TLS만), `pcId` 앞 4바이트가 맞는 PC에 `POST /api/v1/pair {kind: pc}`를 보낸 뒤 `GET /api/v1/endpoints`로 전체 경로를 받는다. 프로토콜 추가는 없다.
- **실시간 공유 (M-46)**: 허브의 트레이가 자기에게 연결한 PC(`kind: pc`)를 보고 있으면, 허브는 그 PC의 inbox 응답에 `live: true`를 싣는다(기다리던 롱 폴은 바로 깨우고, 폴 사이에 온 요청은 다음 폴에 바로 답한다. 요청이 이어지는 동안은 다시 깨우지 않는다). 받은 PC는 자기 `LiveSnapshot`을 2초마다 `POST /api/v1/live-snapshot`(인증, 본문 최대 1MiB, 토큰 확인 뒤에 본문을 읽음)으로 보내고, 응답 `{schema, live}`가 `false`이거나 세 번 연속 실패하면 멈춘다. 허브는 요청을 15초 동안 유지하고(트레이가 1초마다 갱신) 최신 스냅숏 하나만 메모리에 둔다. 휴대폰이 보내면 403, 형식이 틀리면 400 `invalid_snapshot`. 반대 방향(이 PC가 연결한 허브)은 그 허브의 `GET /api/v1/live` WebSocket을 이 PC가 가진 토큰으로 연다. 공유하는 동안 보내는 PC의 트레이에는 "다른 기기가 이 PC를 보는 중"이 켜진다. v0.33.0 이하 PC는 `live`를 모르므로 요약만 보인다.
- **기기 그룹 (M-47)**: 연결은 그룹 가입이다. 모든 경로는 인증(그 PC에 페어링된 기기의 Bearer 토큰)이 필요하다.
  - `GET /api/v1/group` → [`group.v1.json`](mobile-protocol/group.v1.json): `{schema, pcId, pcName, pcs: [{pcId, pcName, token}], removed?: [{key, at}], aliases?: [{key, name, alias?, at}]}`. `pcs`는 이 PC와 어느 방향으로든 연결된 PC, `token`은 이 PC가 그 PC의 토큰을 가져 소개할 수 있는지. `aliases`(v0.38.0, M-51)는 그룹의 별칭 표 전체: `key`는 `pc:<pcId>` 또는 `phone-name:<이름>|<모델>`(소문자), `name`은 정할 때 그 기기가 보고한 이름, `alias`가 없으면 지운 것(90일 보관), `at`은 정하거나 지운 시각(밀리초까지).
  - `POST /api/v1/group/offer` → 이 PC의 새 `PairingOffer`(소개용 일회용 코드: QR 코드와 따로 최대 8개, 5분, 1회용).
  - `POST /api/v1/group/join {schema, offer}` → `{schema, linked, already, pcName?, error?}`: 이 PC가 오퍼의 PC에 연결한다. `error`: `self`·`removed`·`unreachable`·`rejected`.
  - `POST /api/v1/group/introduce {schema, pcId}` → 그 PC의 새 `PairingOffer`(이 PC가 토큰을 가진 PC만, 아니면 404 `not_linked`).
  - `POST /api/v1/group/remove {schema, key}` → `{schema, removed}`: PC만(휴대폰은 403). `key`는 `pc:<pcId>`·`phone:<푸시 해시>`·`phone-name:<이름>|<모델>`. 받은 PC는 해제만 하고 다시 전하지 않는다.
  - `POST /api/v1/group/alias {schema, aliases: [{key, name, alias?, at}]}` → `{schema, changed}` (v0.38.0, M-51): 휴대폰·PC 모두 보낼 수 있다(한 번에 64개까지). 키마다 `at`이 늦은 쪽이 이기고 같으면 별칭 글자가 큰 쪽(서수 비교)이 이긴다. 별칭은 앞뒤 공백 제거·안쪽 공백 한 칸·40 코드 포인트로 맞추고 기기 이름과 같으면 지운 것으로 본다. 하루 넘게 미래인 `at`, 잘못된 키, 빈 이름은 버린다. 받은 PC는 합치기만 하고 다시 전하지 않는다(다른 PC는 2분 동기화 때 받는다). 형식이 틀리면 400.
  - PC는 연결 직후 상대에게 자기 오퍼로 `join`을 보내 되연결을 받고, 2분마다 연결한 PC의 그룹을 읽어 모자란 PC를 `introduce`로 연결하며 `removed`와 `aliases`를 합쳐 적용한다(제거 7일 보관). 트레이에서 정한 별칭은 바로 토큰을 가진 모든 PC에 `group/alias`로 보낸다. presence에는 `pcId`(PC만), `POST /api/v1/pair`에는 `pcId`(PC만, 손으로 다시 연결하면 제거 기록을 지움)가 더해졌다. `GET /api/v1/devices`의 PC 항목에는 v0.38.0부터 그 PC가 보고한 `pcId`가 실린다(별칭을 찾는 키).
  - 휴대폰은 최대 10분에 한 번(당겨서 새로고침은 즉시)과 30분 잡에서 각 PC의 그룹을 읽어, 서로 모르는 두 PC는 `offer`→`join`으로 잇고 모르는 PC는 `introduce`로 받은 오퍼로 페어링한다. 같은 때 자기 PC들의 별칭(`pc:` 키)을 맞춘다: 더 늦은 그룹 항목은 받아들이고, 자기가 더 늦게 정한 것은 `aliases`가 있는(v0.38.0 이상) PC 가운데 그것을 모르는 PC에 보낸다. 사용자가 휴대폰에서 별칭을 정하면 페어링된 모든 PC에 바로 보낸다. 별칭이 공유되기 전에 정한 별칭은 `at`이 0(가장 오래됨)으로 한 번 올라간다.
- **inbox**: 연결된 PC가 롱 폴링한다. `{schema, next, items: [{seq, id, originId, originName, app, title, body?, ts}], live?}` — 허브의 Windows 알림(`originId: "self"`)과 기기에서 받은 알림의 최근 200건·30분, 요청한 기기가 보낸 것은 뺀다. `after`가 허브의 `next`보다 크면(허브 재시작) 처음부터 준다. 받은 PC는 이 항목을 다시 중계하지 않는다(한 단계). `id`는 모든 경로에서 같아서, 두 PC가 서로 연결돼 같은 알림이 두 길로 와도 한 번만 띄운다.

## 푸시 알림 (UnifiedPush, M-19 · M-21)

앱이 UnifiedPush 배포자(ntfy 등)에서 받은 엔드포인트와 RFC 8291 구독 키를 페어링된 각 PC에 등록한다.
PC는 알림마다 그 엔드포인트로 암호문을 POST하고, 배포자가 앱에 브로드캐스트로 넘긴다. 중계 서버는 평문을 보지 않는다.

- **등록** `POST /api/v1/push` 본문([`push-subscription.v1.json`](mobile-protocol/push-subscription.v1.json)):
  `{schema: 1, endpoint, p256dh, auth, mutedCategories?}`. `endpoint`는 `https://` URL, 1000바이트 이하이며 사용자 정보(`user:pw@`)는 거부한다.
  `mutedCategories`(M-25)는 이 휴대폰이 받지 않을 `category` 목록이다(최대 32개, 각 `a-z0-9_-` 1~64자, 아니면 400). 없거나 비면 전부 받는다.
  `features`(v0.49.0, M-62)는 알림 말고 이 앱이 읽는 푸시 종류다(형식은 `mutedCategories`와 같음). `clip`(클립보드 텍스트)과 `share`(PC가 보내는 파일, v0.51.0, M-65)가 있고, PC는 이 값이 있는 휴대폰에만 클립보드를 보낸다. 바뀌면 재등록을 갱신으로 본다.
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
| `category` | `windows-toast`, `process_watch`, `service_watch`, `http_monitor`, `port_monitor`, `event_log`, `advisor`, `boot`, `device-notification`, `agent-waiting`, `agent-done`, `agent-approval`, `test`. `agent-approval`(M-48)은 Claude Code 권한 요청: `priority: critical`, `title`은 할 일("명령 실행", "파일 수정"), `body`는 세션 제목과 실행할 명령·파일(최대 3000자), `source`는 `Claude Code`, 그리고 `approval: {id (32 hex), expiresAt}`가 붙는다. 앱은 잠금 해제가 필요한 허용/거부 버튼을 달고 `POST /api/v1/approval`로 답한다. `agent-*`(M-45)는 AI 코딩 세션이 사용자 입력을 기다리거나 1분 넘게 하던 작업을 마쳤을 때, PC 사용자가 60초 넘게 입력이 없을 때만 온다: `title`은 세션 제목, `body`는 "입력을 기다립니다: <이유>" 또는 "작업을 마쳤습니다 (<걸린 시간> 걸림)"와 폴더 이름, `source`는 `Claude Code`·`Codex` |
| `priority` | `info`·`warning`·`critical`·`recovery` |
| `title`, `body?` | 표시할 텍스트(평문, HTML 아님) |
| `source` | 알림 채널 키. Windows 알림은 원래 앱 이름, AI 세션 알림은 도구 이름, 그 밖은 `category`와 같다 |
| `count` | 묶음 건수(앱별 묶음이면 2 이상) |
| `ts` | 생성 시각(ISO 8601) |

- 앱의 Android 알림 채널은 PC(채널 그룹)마다, `source`마다 하나씩이다. 사용자가 PC·앱 단위로 끌 수 있다.
  채널 id 끝에는 앱의 알림 우선순위(팝업·소리·진동 조합)가 붙고, 우선순위를 바꾸면 같은 PC·`source`의 채널이 새 조합으로 바뀐다(M-37).
- 팝업·소리·진동·묶음·"센터에만"은 휴대폰 앱의 설정이다(M-37). PC는 `priority`를 지금처럼 보내기만 하고 앱의 선택을 모른다.
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

### 클립보드 (M-62)

사용자가 누를 때 한 번 보내는 텍스트다(감시 없음). 설계는 [`CLIPBOARD_SHARE.md`](CLIPBOARD_SHARE.md) 1단계.

- **키 합의**: 기기가 `POST /api/v1/clip/key`에 이 교환에만 쓰는 새 P-256 공개키(65바이트 비압축, base64url)를 보내고, PC가 자기 새 공개키로 답한다. 양쪽이 ECDH 비밀(x 좌표 32바이트)에서 키 = HKDF-SHA256(salt = 0 32바이트, info = `"homebase clip v1"` ‖ 기기 공개키 ‖ PC 공개키, 32바이트)을 만들고, 개인키는 버리고 키만 둔다. 공개키만 오가므로 Bearer 토큰을 보는 Cloudflare 엣지도 키를 만들 수 없다(그 교환을 능동적으로 바꿔치기하지 않는 한). PC는 기기마다 하나를 `clip-keys.json`(DPAPI)에 두고, 다시 합의하면 바꾼다. 휴대폰은 푸시 등록 뒤 미리 합의하고, PC → PC는 처음 보낼 때 합의한다. 픽스처 [`clip-key.v1.json`](mobile-protocol/clip-key.v1.json)은 RFC 8291 부록 A의 두 키(기기 = UA, PC = AS)로 합의한 키다.
- **암호화**: 모든 경로에서 내용을 그 키로 한 번 더 종단 암호화한다(Cloudflare 엣지는 TLS를 푼다). AES-256-GCM, nonce는 메시지마다 12바이트 난수, `data`는 암호문 뒤에 16바이트 태그, 추가 데이터 없음, 둘 다 패딩 없는 base64url.
- **봉투** [`clip.v1.json`](mobile-protocol/clip.v1.json): `{schema, id, nonce, data}`. `id`는 소문자 hex 32자(보낸 쪽이 만듦). 봉인한 내용은 UTF-8 JSON `{schema, text?, chars, ts, sensitive?}` — `chars`는 텍스트 길이(UTF-16 단위), `sensitive`는 비밀번호 관리자가 넣은 내용(받는 쪽은 클립보드 기록·미리보기에서 뺀다). 텍스트는 1~100,000자.
- **휴대폰·PC → PC**: `POST /api/v1/clip`. PC는 그 기기 토큰의 키로 열고 트레이에 넘겨 클립보드에 넣는다. 같은 기기의 같은 `id`는 30분 동안 한 번만 넣고 200을 준다(다른 경로로 재시도해도 안전). 트레이가 클립보드에 넣었다고 답해야 200이다. 넘길 트레이가 없으면(로그인한 사용자 없음) 409 `no_user_session`, 클립보드가 바빠 못 넣었으면 503 `clipboard_busy` — 둘 다 기억하지 않으므로 같은 메시지를 다시 보내면 받는다. 그 기기와 합의한 키가 없으면 409 `clip_key_needed`, 키가 다르면 400 `invalid_clip`: 보내는 쪽은 한 번 다시 합의하고 보낸다. PC끼리는 그룹 링크의 토큰(M-47)으로 보낸다.
- **PC → 휴대폰**: 복호화된 푸시 [`push-clip.v1.json`](mobile-protocol/push-clip.v1.json) `{schema, kind: "clip", pcId, id, nonce, data}`(`title` 없음 — 옛 앱은 버린다), 그 휴대폰과 그 PC가 합의한 키로 봉인, `TTL` 600초, `Urgency: high`. 평문이 한 푸시(3993바이트)를 넘으면 `text`를 빼고 보내며, PC가 10분 동안 메모리에 두어 그 휴대폰만 `GET /api/v1/clip?id=`로 받는다. 앱은 `pcId`가 페어링된 PC이고 그 PC와 합의한 키로 열릴 때만 받고, 같은 `id`는 한 번만, `ts`가 휴대폰 시계로 30분보다 오래됐으면 버린다(TTL을 넘겨 늦게 온 푸시가 클립보드를 바꾸지 않게). `text`가 있으면 바로 클립보드에 넣고 조용한 알림(채널 `hb.clip`, 2분 뒤 사라짐)을 띄우며, 없으면 "눌러서 복사" 알림(10분)을 띄운다. 받는 휴대폰은 `features`에 `clip`을 등록하고 키를 합의한 휴대폰뿐이다.
- **앱에서 보내기**: 공유 메뉴의 Homebase(받은 텍스트 — 다른 앱이 연 것이므로 PC가 하나여도 고르게 함), 빠른 설정 타일(창에 포커스가 온 뒤 클립보드를 읽어 마지막으로 보낸 PC로), PC 카드 메뉴 "클립보드 보내기". 휴대폰 → PC 의 `ts`는 `Instant.toString()`(UTC `Z`, 밀리초가 있으면 소수점) 형식이다.
- 텍스트는 로그에 남기지 않는다(길이와 보낸 기기만). 주고받은 텍스트는 양쪽 알림 센터에 "받은 클립보드"·"보낸 클립보드"로 남는다(M-70): 상대 기기, 길이, 보낸 쪽은 결과, 텍스트 앞 2,000자. `sensitive`이면 텍스트를 남기지 않는다. PC는 받아 클립보드에 넣었을 때와 트레이에서 보낸 대상마다(사라진 대상 빼고), 앱은 클립보드에 넣었을 때(긴 텍스트는 눌러서 받았을 때)와 보낼 때마다 한 건. 앱의 "복사했습니다" 알림을 누르면 알림 센터가 "받은 클립보드" 필터로 열리고 그 항목이 펼쳐진다. 다른 휴대폰에 넘겨 주기만 한 PC(M-67)는 남기지 않는다.

### 파일 (M-64)

기기가 PC로 파일 1~20개를 보낸다(2a). 설계는 [`FILE_SHARE.md`](FILE_SHARE.md). 픽스처 [`transfer.v1.json`](mobile-protocol/transfer.v1.json)을 .NET과 Kotlin이 함께 연다.

- **시작** `POST /api/v1/transfer`: 본문은 클립보드 봉투 `{schema, id, nonce, data}` — `id`는 기기가 정한 전송 id(소문자 hex 32자), `data`는 M-62 합의 키로 봉인한 `{schema, files: [{name, size, mime?}], ts}`. 같은 id로 다시 보내면 그 상태를 돌려준다. 한도: 20개, 파일 2GiB, 합계 4GiB; PC 보관 10GiB, 디스크 여유가 (크기 + 2GiB) 미만이면 507; 기기당 진행 중 4개(넘으면 503 `busy`). 답 `TransferStatus {schema, id, state, chunkBytes, have}` — `state`는 `receiving`·`complete`, `have`는 파일마다 받은 조각 번호 범위(`"0-127,130"`, 없으면 `""`).
- **조각** `POST /api/v1/transfer/chunk?id=&f=&n=`: 평문 조각은 4,194,304바이트(마지막은 나머지, 빈 파일은 조각 없음). 전송 키 = HKDF-SHA256(IKM = 합의 키, salt = 0 32바이트, info = `"homebase transfer v1"` ‖ 전송 id 16바이트, 32바이트); 본문 = AES-256-GCM(nonce = 파일 번호 4바이트 ‖ 조각 번호 8바이트, 빅엔디언, 추가 데이터 없음)의 암호문 ‖ 태그. 위치가 다르거나 길이가 맞지 않으면 400 `bad_chunk`. PC는 조각을 동시에 4개까지 처리하고, 30초 안에 차례가 오지 않으면 503 `busy`(잠시 뒤 같은 조각을 다시).
- **이어 보내기** `GET /api/v1/transfer?id=`: 끊긴 뒤 `have`에 없는 조각만 보낸다. PC가 전송을 잃었으면(409 `unknown_transfer`) 같은 id로 다시 시작한다.
- **끝** `POST /api/v1/transfer/done?id=`: 봉투(같은 id) 안에 `{schema, sha256: [파일별 소문자 hex]}`. 모든 조각과 해시가 맞으면 200 `{schema, saved}` — `tray`(로그인한 사용자의 트레이가 저장 중) 또는 `waiting`(아무도 로그인하지 않음, 24시간 보관). 빠진 조각이 있으면 409 `incomplete`, 해시가 다르면 409 `hash_mismatch`(전송을 버림).
- **취소** `DELETE /api/v1/transfer?id=`. 끝나지 않았거나 저장되지 않은 전송은 24시간 뒤 PC가 지운다.
- **PC 안**: Agent가 `ProgramData\Homebase\mobile\transfers\<id>`에 복호화해 두고, 끝나면 트레이에 넘긴다(브리지 `transfer.ready`). 트레이는 파이프 `Homebase.Transfer`로 읽어 사용자의 다운로드`\Homebase`에 저장한다(이름 정리, `Zone.Identifier` ZoneId=3, 자동으로 열지 않음).
- 모든 요청 본문은 120초 안에 다 와야 한다(그렇지 않으면 400).

### PC가 보내는 파일 (M-65)

PC의 트레이가 보낸 파일(2b). 다른 PC로는 Agent가 위 "파일"(2a) 경로로 올리고, 휴대폰으로는 아래처럼 알리고 휴대폰이 받아 간다.

- **알림**: 복호화된 푸시 [`push-share.v1.json`](mobile-protocol/push-share.v1.json) `{schema, kind: "share", pcId, id, nonce, data}` — `id`는 전송 id, `data`는 합의 키로 봉인한 `{schema, count, totalBytes, name?, ts}`(첫 파일 이름, 100자), `title` 없음, TTL 24시간. `features`에 `share`를 등록하고 키를 합의한 휴대폰에만 보낸다.
- **목록** `GET /api/v1/transfer/offer?id=`: 봉투 [`transfer-offer.v1.json`](mobile-protocol/transfer-offer.v1.json) — `{schema, files: [{name, size, mime?, sha256}], ts}`.
- **조각** `GET /api/v1/transfer/fetch?id=&f=&n=`: 2a와 같은 전송 키(그 휴대폰의 합의 키 + 전송 id)와 위치 nonce로 봉인한 조각, 본문은 원시 바이트(`application/octet-stream`). 휴대폰은 해시가 맞아야 저장한다.
- **받음** `POST /api/v1/transfer/received?id=`: PC의 트레이에 "받았습니다"가 뜬다. 모든 대상이 끝나면 PC가 파일을 지우고, 아니면 24시간 뒤 지운다.

### 원격 명령 (M-69)

휴대폰이 PC가 켠 고정 명령을 요청한다. 설계는 [`REMOTE_COMMANDS.md`](REMOTE_COMMANDS.md), 견본 [`commands.v1.json`](mobile-protocol/commands.v1.json)(목록, 요청 평문과 클립보드 픽스처 키로 봉인한 봉투, 답).

- **목록** `GET /api/v1/commands` → `{schema, commands: [{name, label, kind, confirm}], wake?: {macs: ["aa:bb:…"]}, pending?: {name: "restart", at}}`. `kind`는 `session`·`system`·`custom`, `confirm`이면 앱이 확인과 잠금 해제를 거친다. 이름: `lock`, `screen_off`, `mute`, `volume_up`, `volume_down`, `media_play_pause`, `media_next`, `media_previous`, `restart`, `sleep`, `custom:<id>`. 404면 옛 PC다.
- **요청** `POST /api/v1/commands`: 클립보드 봉투(M-62 합의 키, 새 id) 안에 `{schema, id, command, op, ts}` — `id`는 봉투의 id와 같아야 하고 `op`는 `run`(이 경로)·`cancel`(취소 경로)이다. 둘 다 봉인 안에 있어, 중간에서 요청을 본 쪽이 id만 바꿔 다시 보내거나 취소를 실행으로 돌리지 못한다. 답 `{schema, ok, state?, at?, error?}` — `state`는 `done`·`scheduled`(다시 시작, `at`에)·`cancelled`. 오류는 403 `not_allowed`(꺼짐, 또는 PC가 요청), 400 `invalid_command`, 409 `replayed`(같은 id, `ts` ±2분 밖), 429 `busy`(1분 10번), 409 `no_user_session`, 409 `clip_key_needed`(다시 합의하고 새 id로), 500 `failed`.
- **취소** `POST /api/v1/commands/cancel`: 같은 봉투에 `command: "restart"`, `op: "cancel"` → `cancelled` 또는 409 `nothing_pending`.
- **다시 켜짐**: 다시 시작을 요청한 휴대폰에 PC가 일반 푸시("다시 시작을 마쳤습니다", 범주 `commands`)를 보낸다. PC에서 취소되면 "다시 시작이 취소됐습니다".
- **깨우기**: `wake.macs`로 휴대폰이 같은 LAN에 매직 패킷(0xFF×6 + MAC×16, UDP 9·7, 브로드캐스트)을 보낸다.

### 휴대폰 ↔ 휴대폰 (M-67)

같은 PC에 연결된 다른 휴대폰으로 그 PC를 거쳐 보낸다. 견본 [`share-relay.v1.json`](mobile-protocol/share-relay.v1.json)(봉인 전 평문)을 .NET과 Kotlin이 함께 읽는다.

- **목록** `GET /api/v1/share/phones` → `{schema, phones: [{deviceId, name, clip, files}]}`: 그 PC와 키를 합의했고 푸시를 받는 다른 휴대폰(묻는 휴대폰 제외). `clip`은 클립보드 텍스트를, `files`는 파일을 받을 수 있다는 뜻이다. 404면 옛 PC라 넘기지 못한다.
- **텍스트**: 위 `POST /api/v1/clip`의 봉인한 내용에 `to: [deviceId]`(1~10개)를 넣는다. 그러면 PC는 자기 클립보드에 넣지 않고, 받을 휴대폰마다 위 "PC → 휴대폰" 푸시를 그 휴대폰 키로 봉인해 보낸다(새 `id`, 내용에 `from`: 보낸 휴대폰 이름). 답은 200 `{schema, accepted: true, forwarded: [{deviceId, ok, error?}]}`(`error`: `unknown_target`·`push_failed` 등).
- **파일**: 위 "파일"(2a)과 같고, 봉인한 전송 설명에 `to: [deviceId]`를 넣는다. `done`이 맞으면 PC는 트레이 대신 받을 휴대폰에 "PC가 보내는 파일" 흐름으로 알리고(봉인한 요약에 `from`), 200 `{schema, saved: "forwarded"}`로 답한다. 같은 전송의 `done`을 다시 보내도 같은 답이다. 넘길 휴대폰이 없으면 409 `unknown_target`(받은 것은 지움). 자기 자신은 `to`에 넣을 수 없다(400).
- PC는 보낸 휴대폰의 키로 열어 받는 휴대폰의 키로 다시 봉인하므로 내용을 거쳐 간다. 저장하지 않으며, 파일은 받을 휴대폰이 받을 때까지(최대 24시간) Agent 폴더에 둔다.

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
| `work[].agent?` | AI 세션의 상세(M-44, v0.33.0부터 `kind: agent`에만): `{status?, waitingFor?, statusSince?, model?, effort?, contextTokens?, contextWindow?, totalTokens?, lastTool?, mode?, prNumber?, remoteUrl?, sessionId?}`. `status`는 `busy`·`waiting`·`idle`(모르면 없음), `waitingFor`는 `waiting`일 때 Claude Code가 적은 이유, `contextTokens`는 마지막 요청이 보낸 토큰(컨텍스트 사용량), `contextWindow`·`totalTokens`는 Codex만, `lastTool`은 도구 이름뿐(인자 없음), `mode`는 Claude Code의 기본이 아닌 권한 모드(`plan` 등), `remoteUrl`은 Claude Code 세션의 claude.ai 주소(Remote Control, 앱은 `https://claude.ai/`로 시작할 때만 연다). 각 필드는 도구가 스스로 쓰는 파일에서 오므로 없을 수 있다. 같은 객체가 `claudeSessions[].agent`에도 있다 |
| `hardware` | 하드웨어 패널(M-34). v0.29.0 전 PC는 보내지 않는다. 읽지 못한 값은 0이 아니라 빠진다(null) |
| `hardware.cpuName` · `cpuTempC` · `boardTempC` | CPU 모델명(레지스트리), CPU 패키지 온도(LibreHardwareMonitor 센서가 있을 때), 보드 온도(가장 뜨거운 보드·ACPI 센서). 온도 백엔드는 `/status`와 같은 `temperature` 설정을 따른다 |
| `hardware.gpus[]` | `{name, vendor?, integrated, loadPct?, tempC?, tempMaxC?, vramUsedBytes?, vramTotalBytes?, sharedUsedBytes?, sharedTotalBytes?, powerW?, powerLimitW?, powerPct?, fanPct?, fanRpm?, clockMhz?, memClockMhz?, source?}`. 주 GPU(외장 → VRAM 큰 순)가 먼저다. `windows`(D3DKMT·GPU 성능 카운터, 제조사 무관)와 `nvidia-smi`(전력·클럭·팬)를 이름으로 합치고 `source`에 `nvidia-smi+windows`처럼 적는다. `shared*`는 GPU가 쓰는 시스템 메모리(내장 GPU의 주 메모리), `powerPct`는 보드 전력 한도 대비 비율. PC 설정 `mobile.nvidiaSmi=false`면 `nvidia-smi`를 쓰지 않아 `windows` 값만 온다 |
| `hardware.disks[]` | `{name, totalBytes, usedBytes}` — 2 GB 넘는 고정 드라이브, 최대 8개, `name`은 드라이브 문자(`C:`) |
| `hardware.ping` | `{host, lastMs?, avgMs?, lossPct, samples}` — 보는 동안 2초마다 `mobile.pingHost`(기본 `1.1.1.1`, 빈 값이면 끔)에 ICMP. 최근 30회(1분)의 평균과 손실률, `samples`는 지금까지 모인 횟수 |

`system.netRxBps/netTxBps`는 v0.29.0부터 실제 유선·Wi-Fi 어댑터만 더한다. Tailscale·VPN·Hyper-V/WSL·VMware 가상 어댑터의 트래픽은 물리 어댑터에도 지나가므로 함께 더하면 두 번 셌다.

집계 규칙(`AppAggregator`):
- 부모 PID가 재사용된 경우(부모가 자식보다 늦게 생성됨)는 부모로 보지 않는다.
- 자기 창이 있는 자손은 별도 앱이다.
- `explorer`는 하위 트리를 합치지 않는다.

## Tray ↔ Agent (`\\.\pipe\Homebase.MobileBridge`)

프레임은 4바이트 LE 길이 + UTF-8 JSON `{op, id?, payload?}`이고 최대 1MiB다.
- Agent → Tray: `files.progress {id, key, name, kind, state, sent, total, error?}`(M-65, 파일을 받는 모든 트레이에): 이 PC가 보내는 파일의 대상별 상태 — `sending`·`sent`(PC가 받음)·`notified`(휴대폰에 알림)·`received`(휴대폰이 받음)·`failed`. `transfer.ready {id, source, sourceKind, files: [{index, name, size, mime}]}`(M-64, `files.ready`를 보낸 트레이 하나에): 확인을 마친 받은 파일. 트레이는 `\\.\pipe\Homebase.Transfer`(같은 ACL)에 연결해 프레임 `{id, file}`을 쓰고, 프레임 `{ok, size, error?}`와 원시 바이트 `size`개를 읽는다(연결당 파일 하나). `clip.set {text, source, sourceKind, sensitive}`(M-62, 프레임 `id` 포함, `clip.ready`를 보낸 트레이에만): 다른 기기가 보낸 클립보드 텍스트 — 트레이가 `clip.done`으로 답할 때까지 최대 10초 기다린다. `watch {on, remote}`(`on`: 누군가 보는 중이니 창 목록을 보고, `remote`: 그게 이 PC의 트레이가 아닌 다른 기기라 "보는 중" 표시를 켬, M-46), `toast.show {title, body?, source?, sourceKind?, app?}`(다른 기기 알림을 이 PC에 띄움, M-28. `title`은 "보낸 기기 · 앱", v0.38.0부터 보낸 기기·종류·앱을 따로 실어 트레이가 그룹 별칭으로 바꿔 보인다, M-51), `aliases {aliases}`(그룹 별칭 표 전체, 연결 직후와 바뀔 때마다, M-51), `response {id, ok, payload?, error?}`.
- Tray → Agent: `session.report`(watch on일 때 2초마다), `pairing.begin`, `devices.list`, `devices.revoke {deviceId}`, `status`,
  `notify.test`(Tray의 테스트 메시지를 Agent의 router로 보냄 → `{delivered, channel?, error?}`),
  `devices.overview`(→ `{here: DevicesResponse, hubs: [{pcId, pcName, online, lastContactAt?, status?, devices[], error?, selfDeviceId?}]}`),
  `hubs.link {link}`(다른 PC의 페어링 링크로 연결 → `{linked, pcName?, error?, message?}`), `hubs.unlink {pcId}`(M-29).
  `live.get {target}`(M-46): `target`은 `self` · `hub:<pcId>` · `device:<deviceId>` → `{state, snapshot?, via?, detail?, at?}`, `state`는 `live`·`connecting`·`unreachable`·`unsupported`·`unknown`. 부를 때마다 그 보기를 10초 동안 열어 두고, 아무도 묻지 않으면 닫는다(수집도 멈춤). `devices.overview`에는 `local: [{deviceId, pairedAt, push, muted?}]`가 더해졌다.
  `session.report`에는 `agents[]`(Codex 세션 등 Claude 밖의 작업, M-26)가 더해졌다.
  `aliases.set {key, name, alias?}`(M-51): 트레이에서 정한 별칭 → `true`. Agent가 바로 저장하고 그룹의 PC에 백그라운드로 보내며, 바뀐 표는 `aliases` 푸시로 돌아온다.
  `approval.ask` ApprovalRequest `{id, app, session, project?, toolTitle, detail, expiresAt}` → `{decision}`(M-48, 최대 10분 대기, `null`이면 PC에서 묻기): Tray의 훅 수신기가 보낸다. Agent는 연결의 읽기 루프와 따로 처리해 그동안 다른 메시지가 막히지 않는다. `approval.cancel {id}`(응답 없음): 사용자가 PC로 돌아와 Tray가 기다리기를 그만둠.
  `advise`(M-60) → `advice.v1.json` 과 같은 `AdviceResponse`: 휴대폰과 같은 조언(한 번에 하나, 60초 재사용)을 트레이 설정 › AI 조언에 보인다. 읽기 루프와 따로 처리한다. 이 op 를 모르는 Agent 는 `unsupported`.
  `files.targets` → `{targets}`(파일을 받을 수 있는 곳 — `share` 휴대폰·연결한 PC), `files.begin {targets, files: [{name, size, mime?}]}` → `{id?, error?}`, 이어서 파일마다 `Homebase.Transfer` 쓰기 모드(`{id, file, mode: "write", size}` → 머리 → 원시 바이트 `size`개 → 머리), `files.go {id}` → `true`(모두 들어왔으면 보냄), `files.cancel {id}` (M-65).
  `files.ready`(M-64, 응답 없음): 연결 직후 — 받은 파일을 저장하는 트레이. Agent는 이때 기다리던 전송을 넘긴다. `transfer.saved {id, ok, error?}`(M-64, 응답 없음): `transfer.ready`의 파일을 모두 저장했는지 — 저장했으면 Agent가 지우고, 아니면 다음 트레이 연결 때 다시 넘긴다.
  `clip.done {id, ok}`(M-62, 응답 없음): `clip.set`을 클립보드에 넣었는지. `clip.ready`(M-62, 응답 없음): 연결 직후 트레이가 보낸다 — 받은 클립보드 텍스트를 클립보드에 넣는 트레이라는 뜻. Agent는 이 연결에만 `clip.set`을 보낸다(옛 트레이는 버리므로). `clip.targets` → `{targets: [{key, name, kind}]}`: 보낼 수 있는 곳 — `device:<deviceId>`(여기 페어링되고 `features`에 `clip`이 있는 휴대폰), `pc:<pcId>`(이 PC가 토큰을 가진 PC). `clip.send {targets, text, sensitive}` → `{results: [{key, name, ok, error?}]}`(최대 30초, 읽기 루프와 따로). `error`: `no_user_session`·`clipboard_busy`·`unreachable`·`unsupported`(그 PC가 옛 버전)·`rejected`·`push_failed`·`unknown_target`·`invalid_clip`·`failed`.
  `agent.alert {kind, app, title, project?, detail?}`(M-45, 응답 없음): AI 세션이 입력을 기다리기 시작했거나(`agent-waiting`) 1분 넘게 하던 작업을 마쳤을 때(`agent-done`), 사용자가 60초 넘게 입력이 없으면 Tray가 보낸다. Agent는 길이를 자르고(앱 40, 제목 200, 폴더 260, 상세 300자) 같은 알림은 60초에 한 번, 하루 60건까지 암호화 채널(`EncryptedOnly`)로 푸시한다.
- 그 밖의 op는 연결을 끊는다.
