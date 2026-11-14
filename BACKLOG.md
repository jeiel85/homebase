# Backlog

범위 밖으로 넘긴 후속 작업이다. 항목마다 무엇을, 왜, 영향 범위를 적는다.

## 알림 채널 M-19 2단계 이후 (2026-11-13, UnifiedPush 1단계 PR에서 이월)

- [ ] **Telegram 제거(2단계)**: 실사용에서 놓친 푸시가 없다고 확인되면 `TelegramOutboundChannel`, `TelegramPollingService`, 명령 20개를 걷어 낸다. Telegram 평문 클라우드를 없애는 것이 M-19의 목표다. 영향: Agent, Core 명령, 문서.
- [ ] **설치 마법사에서 Telegram 필수 해제**: `installer/setup.ps1`과 Tray 온보딩이 봇 토큰을 필수로 받고, 연결 확인도 Telegram으로 한다. 이를 선택 사항으로 바꾼다. 푸시만 쓰는 사용자가 설치를 끝낼 수 없기 때문이다. 영향: installer, Tray 온보딩·설정, README.
- [ ] **`/mute`를 Tray 로컬 메뉴로 이전**: Telegram을 제거하면 음소거할 방법이 없어진다. 앱은 읽기 전용으로 남긴다(M-19). 영향: Tray, AlertPolicy.
- [ ] **앱 알림 이력 화면**: Telegram을 제거하면 `/alerts`가 사라진다. 대신 PC 알림 로그(SQLite)를 인증 API로 읽기 전용 조회한다. 영향: Agent API, 앱, 프로토콜 픽스처.
- [ ] **VAPID 지원**: VAPID를 요구하는 배포자(Mozilla autopush 기반 등)는 지금 `REGISTRATION_FAILED(VAPID_REQUIRED)`로 실패한다. 이를 지원하려면 PC별 ES256 키, 등록 시 `vapid` extra, PC가 보내는 `Authorization: vapid` 헤더가 필요하다. 영향: 앱 connector, PC 채널, 프로토콜.
- [ ] **배포자 선택을 Link Activity(`unifiedpush://link`)로**: AND_3은 처음 고를 때 Link Activity를 권장한다. 지금은 설치된 배포자 목록에서 앱이 직접 고른다. 영향: 앱 PushSettings.
- [ ] **토스트 예산 초과를 휴대폰에 알리기**: 일일 예산을 넘긴 토스트는 버리고 Agent 로그에만 남는다. 예산이 회복될 때 "N건 생략" 요약 한 건을 보내는 안이 있다. 영향: NotificationForwardingService.
- [ ] **Tray 설정 UI에 묶음·예산·Telegram 대체 옵션 노출**: `bundleSeconds`, `maxPushesPerDay`, `telegramFallback`은 지금 설정 파일에서만 바꿀 수 있다. 영향: Tray 설정 창, NotificationForwardingConfigurator.

## Tray 설정 창 일원화 이후 (2026-11-13, 휴대폰 탭 PR에서 이월)

- [ ] **휴대폰 탭 문구 현지화**: 휴대폰 탭 본문(상태 안내, 경로 설명, 대화상자)은 예전 "휴대폰 연결" 창에서 옮긴 그대로 한글 고정이다. 다른 탭처럼 `L.cs`로 옮겨 영어 Windows에서도 영어로 보이게 한다. 영향: `PhoneConnectPanel`, `L.cs`.

## PC 경로 푸시 M-23 이후 (2026-11-14, 재연결 수정 PR에서 이월)

- [ ] **다시 페어링하면 PC에 옛 기기 항목이 남음**: 같은 휴대폰을 QR로 다시 연결하면 새 기기 id가 생기고 옛 항목(같은 푸시 엔드포인트 포함)이 그대로 남는다. 그래서 알림 푸시가 같은 휴대폰에 두 번 나가 ntfy 한도를 두 배로 쓴다(앱은 같은 알림 id라 한 번만 보인다). 등록 시 같은 엔드포인트를 가진 다른 기기의 구독을 지우거나, Tray에서 옛 항목을 정리하도록 안내한다. 영향: `MobileApiServer` `/api/v1/push`, `MobileDeviceStore`, Tray 휴대폰 탭.
- [ ] **알린 경로를 디스크에 기억**: `EndpointAnnouncer`는 마지막으로 알린 목록을 메모리에만 두므로, 경로가 그대로여도 Agent가 시작할 때마다 한 건 보낸다. `state.json`에 두면 Cloudflare를 쓰지 않는 PC는 재시작 푸시가 없어진다. 영향: `EndpointAnnouncer`, `MobileDeviceStore`.
