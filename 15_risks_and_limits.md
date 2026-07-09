# 15. Risks and Limits

## 1. Windows Toast 알림 한계

Windows Notification Listener는 사용자가 알림 접근 권한을 승인해야 한다. 또한 모든 앱 내부 이벤트를 볼 수 있는 것이 아니라 Windows 알림 시스템에 표시되는 notification 중심으로 접근한다.

따라서 다음은 보장하지 않는다.

- 앱 내부에서만 표시되고 Windows 알림으로 나오지 않는 메시지
- 알림에 포함되지 않은 원문 데이터
- 보안 정책상 감춰진 내용

## 2. Windows Service 세션 한계

Windows Service는 사용자 데스크톱과 분리되어 있으므로 사용자 UI와 직접 상호작용하는 기능은 Tray App이 담당해야 한다.

## 3. Telegram 보안 리스크

Telegram bot token이 유출되면 제3자가 봇 API를 호출할 수 있다. 따라서 다음이 필수다.

- token을 repo에 넣지 않기
- 환경변수 또는 DPAPI 사용
- chat_id allowlist 사용
- unauthorized 요청 audit

## 4. 알림 과다 리스크

EventLog, Toast, disk warning은 폭주할 수 있다.

대응:

- deduplication
- rate limit
- quiet hours
- severity filter
- provider/app filter

## 5. 개인정보 리스크

Windows 알림에는 개인 메시지, 인증번호, 금융정보, 보안 알림이 포함될 수 있다.

대응:

- forwarding 기본 off
- 민감 앱 block
- masking 기본 on
- allow list 모드 제공
- Telegram 전송 전 body truncate

## 6. 원격 제어 리스크

사용자 요구가 커지면 원격 명령 실행을 넣고 싶어질 수 있다. 하지만 개인 PC bot에 `/shell` 같은 기능을 넣으면 token 유출 시 피해가 매우 크다.

권장:

- 초기 버전에서는 절대 구현하지 않는다.
- 필요하면 별도 프로젝트/별도 threat model로 다룬다.
- 최소한 confirmation, short-lived token, audit, command allowlist를 둔다.

## 7. 성능 리스크

너무 짧은 polling interval이나 WMI/EventLog 과도 사용은 PC 성능에 영향을 줄 수 있다.

권장 기본값:

- metric interval: 60초
- watch interval: 60초
- toast polling: 3초 이상
- event log polling fallback: 30초 이상

## 8. 백신/보안 솔루션 오탐 가능성

PC 상태 감시, 프로세스 감시, 알림 읽기, Telegram 통신을 하는 프로그램은 일부 보안 솔루션에서 의심할 수 있다.

대응:

- 기능과 권한을 투명하게 문서화
- keylogging/stealth behavior 금지
- 설치 경로와 로그 제공
- code signing은 장기 목표

### 8-1. 온도 센서 드라이버(WinRing0) 탐지 — 확인된 사례

온도 모니터링에 LibreHardwareMonitor를 쓰면 하드웨어 센서 접근을 위해 **WinRing0 커널 드라이버**를 로드한다. 이 드라이버는 임의 커널 포트·메모리 I/O를 허용해 Microsoft **취약 드라이버 차단 목록**에 올라 있고, Windows Defender가 `VulnerableDriver:WinNT/Winring0`(심각/트로이)로 탐지한다. LHM이 호스트 프로세스명으로 드라이버를 추출하므로 파일명은 `Homebase.Agent.sys`로 보이지만, 탐지는 파일명이 아니라 **시그니처** 기준이라 이름을 바꿔도 회피되지 않는다.

대응(현재 구현):

- **기본 온도 백엔드를 드라이버 없는 WMI/ACPI(`temperature.source = "Wmi"`)로 전환** → 신규 설치에서 드라이버를 로드하지 않아 탐지가 뜨지 않는다. 다만 많은 데스크톱은 ACPI 존 온도를 노출하지 않아 값이 비어 있을 수 있다.
- **정밀 센서는 opt-in**: `installer/enable-temperature.ps1`(관리자)이 `source`를 `LibreHardware`로 바꾸고 드라이버에 대한 Defender 예외를 등록하며, 언인스톨/`-Disable` 시 예외를 정리한다.
- **한계**: Defender 예외는 AV *탐지*만 억제한다. 취약 드라이버 차단 목록·HVCI(메모리 무결성)·Smart App Control이 켜진 PC에서는 예외와 무관하게 드라이버 로드가 커널 수준에서 차단되어 온도가 여전히 비어 있을 수 있다. `enable-temperature.ps1`은 이 상태를 감지해 사전 경고한다.

## 9. 네트워크 단절

Telegram API 접근이 안 되면 알림 전송이 불가능하다.

대응:

- local alert queue
- retry/backoff
- 복구 시 summary 전송 옵션

## 10. “상용화” 관점에서 부족한 점

이 설계는 개인용/1인 운영용으로는 충분하지만, 다중 사용자 SaaS로 상용화하려면 다음이 추가로 필요하다.

- 중앙 계정 시스템
- agent enrollment
- device identity
- encrypted cloud relay
- multi-tenant dashboard
- audit/compliance
- signed installer
- auto-update server
- privacy policy/terms
- abuse handling

현재 설계는 의도적으로 “내 PC 개인 관제”에 최적화되어 있다.
