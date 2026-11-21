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

- [ ] **알린 경로를 디스크에 기억**: `EndpointAnnouncer`는 마지막으로 알린 목록을 메모리에만 두므로, 경로가 그대로여도 Agent가 시작할 때마다 한 건 보낸다. `state.json`에 두면 Cloudflare를 쓰지 않는 PC는 재시작 푸시가 없어진다. 영향: `EndpointAnnouncer`, `MobileDeviceStore`.

## 기기 공유 M-26~M-29 이후 (2026-11-14, 다중 기기 PR에서 이월)

- [ ] 휴대폰 쪽 실기기·에뮬레이터 E2E — 이번엔 PC 커밋 메모리 여유가 2.2GB뿐이고 에뮬레이터 5560은 다른 세션이 쓰는 중이라 못 했다(PC 쪽은 루프백 두 Agent 통합 테스트로 검증). 알림 접근 권한 → 전달, 다른 기기 목록, 30분 잡, 작업 탭 표시를 기기에서 확인.
- [ ] 연결한 PC가 Quick Tunnel만 열려 있으면 그 PC 재시작 뒤 끊긴다 — 휴대폰은 M-23 푸시로 따라가지만 PC는 그 푸시를 받지 않는다. PC↔PC에도 새 경로 알림(서명된 메시지를 inbox에 싣기 등).
- [ ] 휴대폰이 PC에 닿지 않을 때 그 순간의 휴대폰 알림은 버린다(대기열 없음). 필요하면 짧은 재시도 대기열.
- [ ] 휴대폰 알림 전달 앱 선택이 "보낸 적 있는 앱"에서만 된다 — 설치된 앱 전체 목록에서 미리 고르기.
- [ ] 트레이 대시보드의 "기기" 목록에서 같은 이름의 휴대폰이 두 PC에 붙어 있으면 이름으로만 합친다 — 앱처럼 push 해시로 합치기.
- [ ] Gemini/Antigravity CLI 세션은 기록에 제목이 없어 작업 탭에 넣지 않았다(M-26). 그 CLI가 제목을 남기게 되면 추가.

## 기기 연결 앱 벤치마크 후보 (2026-11-21, M-71 PR에서 이월 — 근거 `docs/DEVICE_APPS_BENCHMARK.md`)

- [ ] **같은 채널 재설치는 묻지 않고 대체**: 페어링 요청에 `ANDROID_ID`의 HMAC(인증에는 쓰지 않음)을 싣고, 같은 값의 옛 항목을 그룹 전체에서 뺀다. M-71은 확인을 받아야 하는데, 이 값은 서명 키가 같으면 재설치해도 유지되므로 같은 채널 재설치는 확인 없이 정리할 수 있다. GitHub판↔Play판 전환은 키가 달라 여전히 M-71이 맡는다. Play Data safety의 "기기 또는 기타 ID" 신고 필요 여부를 먼저 확인한다. 영향: 앱 페어링, `PairRequest`, `MobileDeviceStore`, 프로토콜 픽스처.
- [ ] **오래 안 보인 기기 표시**: 기기 탭·휴대폰 앱 PC 목록에 "마지막 소식 N일 전" 배지(예: 14일)와 정리 버튼. 분실·처분·공장 초기화한 휴대폰처럼 M-71이 못 잡는 경우를 정리한다. 자동 삭제는 하지 않는다. 영향: Tray 기기 탭, 앱 PC 목록.
- [ ] **휴대폰 찾기(벨 울리기)**: 트레이에서 누르면 무음·진동이어도 알람 소리로 울리고 끄기 화면을 띄운다. 기존 봉인 명령·푸시 경로를 쓴다. 방해 금지 우회 방식과 완전 무음에서 울리는지는 실기기로 확인해야 한다. 영향: 앱 푸시 처리, Tray 휴대폰 상세, 원격 명령(M-69).
- [ ] **받은 링크 바로 열기**: 보낸 텍스트가 URL 하나면 받는 쪽에 [열기](휴대폰은 알림 버튼 — Android 10+는 백그라운드에서 자동으로 못 엶, PC 자동 열기는 사용자 옵션). 영향: 클립보드 수신(M-62) 양쪽.
- [ ] **휴대폰 알림 지우기 동기화, 답장은 지원 앱만**: PC에서 닫은 휴대폰발 알림을 휴대폰에서도 지우고 반대도. 원 알림에 RemoteInput이 있으면 PC에서 답장. 영향: 알림 중계(M-28), 앱 `NotificationForwarder`.
- [ ] (P2) 휴대폰 배터리 부족·충전 완료 PC 알림 / 통화가 오면 PC 미디어 일시정지 / PC에서 휴대폰 미디어 제어 / 휴대폰 교체 시 설정 넘기기. 근거와 제약은 벤치마크 문서 4절.
