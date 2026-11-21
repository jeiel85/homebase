# Homebase 벤치마크 — 기기 연결 앱 조사 (2026-11-21)

조사 범위: 공식 문서·스토어 설명·릴리스 노트·공식 버그 트래커를 우선했고, 커뮤니티 글은 그렇다고 표시했다. 출처로 확인하지 못한 것은 **미확인**으로 적었다. 웹 조사만 했고 코드는 건드리지 않았다(Homebase 쪽은 `DECISIONS.md`·`CHANGELOG.md`를 읽어 현황만 확인).

표기: ● 지원 / ○ 부분·조건부 / — 없음(확인한 기능 목록에 없음) / ? 미확인(출처로 확인 못함)

---

## 1. 앱별 요약

| 앱 | 플랫폼 | 연결 방식 | 핵심 기능 | 라이선스·가격 | 2025~2026 상태 |
|---|---|---|---|---|---|
| **KDE Connect** (+GSConnect, Valent) | Linux·Windows·macOS·Android·iOS | LAN(UDP 탐색 + TLS), Bluetooth(느림), IP 직접 추가, VPN 가능. 기기 신원 = 무작위 UUID + 자가서명 인증서(CN=기기 ID), 페어링 때 인증서 저장(TOFU 후 핀) | 알림 양방향·답장, SMS 읽기/보내기, 클립보드(Android 10+는 수동 버튼·타일), 파일·URL·텍스트 공유, 원격 입력(터치패드·키보드), 미디어 제어, 통화 중 미디어 정지, 휴대폰 벨 울리기, 명령 실행, 프레젠테이션 리모컨, SFTP 파일 탐색, 배터리 표시 | 오픈소스, 무료 | 데스크톱 26.08.3(2026-11-05). 2025년 프로토콜 v8, 기기 ID 형식 강제, 보안 권고 다수(CVE-2025-66270 기기 사칭 등) |
| **Microsoft Phone Link / Link to Windows** | Windows 10/11 ↔ Android·iOS | Microsoft 계정 + QR, Wi-Fi·Bluetooth·클라우드 | 메시지·통화·알림·사진, 앱·화면 미러링(일부 기종), 교차 복사·붙여넣기(삼성·HONOR 등 일부), Instant Hotspot(삼성), 시작 메뉴 휴대폰 패널(2025-05 배포), Cross-device Resume(2025~, 2026-01 기준 Spotify·WhatsApp 등), 탐색기에서 휴대폰 파일, 휴대폰을 웹캠으로 | Windows 내장, 무료 | 활발 |
| **Intel Unison** | Windows ↔ Android·iOS | 계정 + QR | 파일·사진·메시지·통화·알림 | 무료 | **2025-06-30 종료**(Lenovo Aura는 2025년 말까지) |
| **Samsung Flow / 연속성** | Galaxy 폰·탭·Galaxy Book | 같은 Samsung 계정 + 같은 Wi-Fi + Bluetooth | Multi Control(키보드·마우스 공유, One UI 5.1+·2021년 이후 Galaxy Book), 앱 이어하기, 클립보드 공유, Quick Share | 무료(삼성 기기 한정) | Samsung Flow 자체의 단종 여부는 **미확인**(비공식 블로그만 "개발 중단"이라 함) |
| **Quick Share (Google, Windows)** | Android ↔ Windows | 근거리(BT·Wi-Fi), 가시성 `모든 사용자/내 기기/연락처/숨김` | 파일 보내기·받기. **같은 Google 계정 기기끼리는 자동 수락** | 무료 | 삼성 Quick Share 호환으로 이름 통합, 삼성 PC용 Google 앱 지원은 2025-05-28 종료 |
| **Pushbullet** | Android·브라우저·Windows(현재 배포 여부 미확인) | 클라우드 계정, 선택적 E2E(비밀번호 기반 AES-256-GCM) | 푸시(노트·링크·파일), 알림 미러링·지우기 동기화, 유니버설 복사/붙여넣기, SMS, 채널 | 무료(월 500 푸시) + Pro(2015년 기준 $4.99/월; 현재 가격 **미확인**) | 현재 개발 활동 **미확인** |
| **Join (joaoapps)** | Android·Chrome·Firefox·Windows/Mac/Linux 데스크톱·웹 | Google 계정 + 클라우드 푸시, 선택적 E2E | 알림 동기화·답장, 클립보드(수동/자동), SMS, 링크·파일 푸시, Tasker·IFTTT·MQTT·API | 30일 체험 후 1회 구매 | 유지 중(3.0.x) |
| **AirDroid Personal** | Android·iOS ↔ Windows·Mac·웹 | 계정 + 클라우드(원격) / LAN(Nearby·Lite 모드) | 파일 전송, SMS·알림, 원격 제어·미러링, 휴대폰 찾기, 원격 카메라(프리미엄) | 무료(기기 2대) / 프리미엄(기본 3대, 최대 10대) | 활발 |
| **LocalSend** | 전 플랫폼 | LAN 멀티캐스트 + HTTPS(지문 = 인증서 SHA-256) | 파일·텍스트, 즐겨찾기 기기, PIN, **v1.18.0(2026-08-10): 링크로 받기·CLI·즐겨찾기 자동 수락 기본·체크섬·Android 백그라운드 전송·빠른 설정 타일** | 오픈소스, 무료 | 활발 |
| **Snapdrop / PairDrop** | 브라우저(PWA) | WebRTC, 같은 망 자동 발견; PairDrop은 6자리 코드·QR로 **영구 페어링**(공유 비밀), TURN 경유 인터넷 전송 | 파일·텍스트, 임시 공개 방, 페어링 기기 편집·해제, 이름 변경 | 오픈소스, 무료, 자체 호스팅 | PairDrop 활발 |
| **scrcpy / QtScrcpy** | Windows·macOS·Linux ↔ Android(ADB) | USB/TCP ADB | 화면·오디오 미러링·제어, 3.0 가상 디스플레이, 카메라 미러링, 3.2 오디오 소스 확대, 3.3 Android 16 오디오 수정 | 오픈소스, 무료 | 3.3.1(2025) |
| **Apple Continuity** (기준점) | Apple 기기 | 같은 Apple 계정 + BT·Wi-Fi | Handoff, 유니버설 클립보드, AirDrop, Sidecar·Universal Control, iPhone 미러링, Continuity Camera, Instant Hotspot | OS 내장 | — |
| **Unified Remote** | 서버: Win·Mac·Linux / 앱: Android·iOS | Wi-Fi, Bluetooth(Windows만), 자동 서버 탐지 | 리모컨 18종(무료)·40종+(유료), WoL, 위젯·빠른 동작·Tasker·Wear OS·NFC, Screen Viewer | 무료 + 1회 구매 | 유지 |
| **Chrome Remote Desktop / RustDesk** | 전 플랫폼 | CRD: Google 계정 / RustDesk: ID + 공용·자체 서버 | 원격 데스크톱, 파일 전송, 클립보드. RustDesk Pro: 웹 콘솔·주소록·기기 관리 | CRD 무료 / RustDesk OSS + Pro | — |
| **Syncthing** | 전 플랫폼 | P2P, 기기 ID = 인증서에서 파생 | 폴더 동기화, introducer(소개자) | 오픈소스 | — |
| **Tailscale Taildrop** | 전 플랫폼 | tailnet | 자기 기기끼리 파일 보내기(공유 메뉴·탐색기 우클릭), 실패 후 1시간 내 이어받기 | Tailscale 요금제 내, Taildrop은 알파 | — |
| **CrossPaste** | Win·Mac·Linux(+Chrome 확장) | LAN 전용·서버 없음, 비대칭 E2E | 텍스트·HTML·RTF·이미지·파일 클립보드, 검색 가능한 이력, OCR, CLI·MCP | AGPL-3.0 | 모바일 지원은 제3자 글만(미확인) |
| **ClipCascade** | Win·Mac·Linux·Android | 자체 호스팅 서버(+선택 P2P/STUN), 기본 E2E | 텍스트·이미지·파일 클립보드. Android는 ADB/Shizuku로 `READ_LOGS`·오버레이 권한을 얻어 자동 감지 | GPL-3.0 | — |

---

## 2. 기능 매트릭스

열 약어: HB=Homebase, KC=KDE Connect, PL=Phone Link, PB=Pushbullet, JN=Join, AD=AirDroid, LS=LocalSend, PD=PairDrop, QS=Quick Share, SS=Samsung 연속성, UR=Unified Remote, SC=scrcpy, AP=Apple(참고)

| 기능 | HB | KC | PL | PB | JN | AD | LS | PD | QS | SS | UR | SC | AP |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 휴대폰 알림 → PC | ● | ● | ● | ● | ● | ● | — | — | — | ? | — | — | ● |
| PC 알림 → 휴대폰 | ● | ● | — | ? | ? | — | — | — | — | — | — | — | — |
| **PC에서 휴대폰 알림에 답장** | — | ○(일부 메신저) | ● | ○(Pro) | ● | ? | — | — | — | — | — | — | ● |
| **알림 지우기 양방향 동기화** | — | ? | ? | ● | ? | ? | — | — | — | — | — | — | — |
| SMS 읽기·보내기(PC) | — | ● | ● | ● | ● | ● | — | — | — | ? | — | — | ● |
| 통화 알림·PC에서 통화 | — | ○(알림만) | ● | — | ? | ? | — | — | — | ? | — | — | ● |
| **통화 중 PC 미디어 정지** | — | ● | ? | — | — | — | — | — | — | — | — | — | — |
| 클립보드 수동 보내기 | ● | ● | — | ● | ● | ? | ● | ● | — | — | — | ? | — |
| 클립보드 자동 동기화 | — | ○(PC→폰, 폰→PC는 수동) | ○(일부 기종) | ○(Pro) | ○ | ? | — | — | — | ● | — | ? | ● |
| 클립보드 이력 | ● | — | — | — | — | — | — | — | — | — | — | — | — |
| 파일 보내기(양방향) | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | — | ? | ● |
| 이어 보내기 | ● | ? | ? | ? | ? | ? | ? | ? | ? | ? | — | — | ? |
| **링크 보내면 상대에서 바로 열기** | — | ● | ●(폰→PC) | ● | ● | ? | — | — | — | ○(삼성 인터넷 이어하기) | — | — | ● |
| 앱·작업 이어하기(Handoff) | — | — | ○(Resume, 앱 한정) | — | — | — | — | — | — | ● | — | — | ● |
| PC 원격 명령(잠금·전원 등) | ● | ● | — | — | ○(Tasker) | — | — | — | — | — | ● | — | — |
| PC 미디어·볼륨 제어 | ● | ● | — | — | ? | — | — | — | — | — | ● | — | — |
| **PC에서 휴대폰 미디어 제어** | — | ? | ○(오디오 플레이어, 미확인) | — | — | — | — | — | — | — | — | — | — |
| **휴대폰을 터치패드·키보드로** | — | ● | — | — | — | — | — | — | — | ●(Multi Control) | ● | — | ● |
| Wake-on-LAN | ● | — | — | — | — | — | — | — | — | — | ● | — | — |
| **휴대폰 찾기(벨 울리기)** | — | ● | — | ? | ? | ● | — | — | — | — | — | — | ● |
| 휴대폰 배터리 등 상태 보기 | ● | ●(배터리) | ●(시작 메뉴) | — | ? | ● | — | — | — | — | — | — | ○ |
| **배터리 부족·충전 완료 알림(PC)** | — | ? | ? | — | ? | ? | — | — | — | — | — | — | — |
| 휴대폰 화면 미러링·제어 | — | — | ○(기종 한정) | — | — | ● | — | — | — | ● | — | ● | ●(Mac) |
| PC 화면 보기(휴대폰에서) | — | — | — | — | — | — | — | — | — | — | ○(유료) | — | — |
| 휴대폰 파일을 PC에서 탐색 | — | ●(SFTP) | ●(탐색기, Android 11+) | — | — | ? | — | — | — | ? | — | — | — |
| 휴대폰 카메라를 PC 웹캠으로 | — | — | ● | — | — | ○(유료) | — | — | — | ? | — | ●(카메라 미러링) | ● |
| 새 사진 알림·최근 사진 | — | — | ● | — | — | ? | — | — | — | ? | — | — | ● |
| 여러 PC 모니터링·경보 | ● | — | — | — | — | — | — | — | — | — | — | — | — |
| 인터넷 경유(같은 망 아님) | ● | ○(VPN) | ● | ● | ● | ● | — | ●(TURN) | — | — | — | ○ | ○ |
| 종단 암호화 | ● | ●(TLS) | ? | ○(선택) | ○(선택) | ? | ●(TLS) | ●(WebRTC) | ? | ? | ○ | — | ● |
| 계정 없이 동작 | ● | ● | — | — | — | — | ● | ● | ○ | — | ● | ● | — |
| **기기 "마지막 연결" 영속 표시** | ○(메모리, 2분 온라인 판정) | ? | ? | ○(목록이 수정 시각순) | ? | ? | — | — | — | — | — | — | ● |
| **재설치 기기 자동 병합** | ○(같은 푸시 엔드포인트만) | — | — | ●(fingerprint) | ○(앱 데이터 복원 시 ID 유지) | ? | — | — | — | — | — | — | ● |

Taildrop·Syncthing·CrossPaste·ClipCascade·RustDesk는 단일 기능 도구라 위 표에서 뺐다(1절 참고).

---

## 3. 기기 관리·재설치 처리 비교

Homebase의 현상: 휴대폰 앱을 지웠다 다시 설치해 연결하면 PC의 연결된 기기 목록에 예전 설치본이 남는다. `DECISIONS.md` M-24는 **같은 푸시 엔드포인트**를 가진 옛 항목을 자동 철회하지만, 재설치하면 UnifiedPush 엔드포인트도 새로 생기므로 이 규칙이 걸리지 않는다. (검토 정정) `lastSeenAt`은 `MobileDeviceStore`가 `state.json`에 10분 단위로 저장하므로 PC를 재시작해도 남는다(설정 › 휴대폰의 "최근 MM-dd HH:mm"). 메모리에만 있는 것은 presence 보드의 `online`·상태 값이다. 이 조사 뒤 v0.58.0(M-71)에서 "같은 이름·모델이 나중에 페어링됐고 그 뒤로 소식 없는 항목"을 표시하고 확인 후 정리하게 했다.

### 3.1 앱별 방식

| 앱 | 기기 ID가 묶이는 대상 | 같은 기기 재설치·재페어링 | 오래 안 보인 기기 | 연결 해제 |
|---|---|---|---|---|
| **KDE Connect** | **설치마다 만드는 무작위 UUID**(하이픈 없이 32~38자, 2025-01 형식 강제). 그 ID가 TLS 인증서 CN. 페어링하면 상대 인증서를 저장하고, 이후 그 인증서와 맞을 때만 연결(TOFU 후 핀) | **덮어쓰지 않음.** 새 ID라 새 기기로 보이고, 옛 항목은 "기억된 기기"로 남아 "연결할 수 없음"이 된다. 공식 버그에 같은 휴대폰이 "이름·키는 같고 ID가 다른" 두 항목으로 남고, 옛 항목 해제 시 크래시 난 사례가 있다(Bug 502915, 505587). 2017년 메일링리스트에서도 재설치 후 중복이 "기억된 기기"에 남는다는 보고. 버그 387532: ID가 바뀌면 옛 인증서가 남아 페어링이 깨짐 → "ID가 다르면 인증서 재생성" 수정 | 자동 정리·표시 없음. 사용자가 해제하거나 `~/.config/kdeconnect` 설정을 직접 지움. 초기 인증서는 10년 유효로 만료 시 조용히 끊김(meta issue #7) | 패킷 `pair:false`(응답 없음). 연결 안 된 기기는 로컬에서만 지움 |
| **Phone Link / Link to Windows** | **Microsoft 계정**에 연결. 계정 단위로 여러 휴대폰·여러 PC 연결 가능 | 공식 문서는 중복을 다루지 않음. Microsoft Q&A(외부 모더레이터)에서 PC 재설치·리셋 뒤 휴대폰 앱에 같은 PC가 "Linked PCs"로 3개 쌓였고 **"현재 Link to Windows 앱에서 예전 PC를 지울 수 없다"**고 답변. 앱을 리셋해도 같은 계정으로 다시 로그인하면 옛 PC가 돌아온다는 보고 | 자동 정리 없음(확인된 범위) | **양쪽에서 각각 지워야 함**(휴대폰: Link to Windows › PC 제거 / PC: 설정 › 모바일 장치 › 장치 관리 › 제거). Bluetooth 페어링은 별도. 제거하면 그 계정의 교차 기기 기능 전체에서 빠진다 |
| **Pushbullet** | 계정 + 기기 객체(`iden`). 기기 생성 시 **`fingerprint`(플랫폼별 문자열) — "앱이 중복 기기를 피하는 데 쓴다"**고 API 문서에 명시. 별도로 E2E 키 지문 `key_fingerprint` | fingerprint가 같으면 기존 기기를 재사용하는 설계(문서 문구 기준). Android 앱이 fingerprint에 무엇을 넣는지는 **미확인** | 삭제된 기기는 `active:false`로 남는 소프트 삭제. 기기 목록은 최근 수정순. 자동 정리 여부 **미확인** | 웹 Settings › Devices(이름 변경은 공식 도움말 확인, 삭제 버튼 위치는 미확인) / API `DELETE /v2/devices/{iden}` |
| **Join** | 기기 ID는 **앱 설치 데이터**에 저장(개발자 답변: 백업 복원으로 옛 폰 데이터를 새 폰에 옮기면 두 기기가 **같은 ID**가 됨, 옛 폰을 리셋하면 새 ID). API 키는 계정 단위 | 백업 복원 시 같은 ID → 사실상 "덮어쓰기"(오히려 두 기기가 충돌). 일반 재설치 시 동작은 **미확인** | **미확인** | 앱·웹의 기기 삭제 경로 **미확인** |
| **AirDroid Personal** | AirDroid 계정에 기기 "바인딩". 요금제별 대수 제한(무료 2대, 프리미엄 3대~최대 10대) | 재설치가 슬롯을 새로 먹는지 **미확인**. 대수 제한 때문에 사용자가 정리하도록 유도되는 구조 | 자동 정리 **미확인** | 지울 기기에서 로그아웃, 또는 "My devices"에서 선택 → 계정 비밀번호 입력 → **즉시 언바인드**(이 경로는 Parental Control 도움말 기준) |
| *(참고)* **Tailscale** | 노드 키(로컬 상태). 상태를 지우고 다시 설치하면 새 노드 | 새 노드가 `이름-1`처럼 **번호 붙은 이름**과 새 IP를 받고 옛 항목은 남음. 문서 권고: 옛 항목 삭제 후 새 노드를 원래 이름으로 변경. 복제된 노드는 "Duplicate node key" 배지 | 콘솔에 마지막 접속 시각 표시. **일반 기기는 자동 삭제 없음**, ephemeral 노드만 비활성 30~60분 뒤 자동 삭제 | 관리 콘솔 ⋯ › Remove / API. 클라이언트 제거만으로는 tailnet에서 빠지지 않음 |
| *(참고)* **Syncthing** | 인증서에서 파생한 기기 ID. 키 파일을 지우면 새 ID | 새 ID = 새 기기 | "30일 안 보인 기기 자동 삭제"는 2021년 **기능 요청**만 확인(구현 여부 미확인). introducer가 지운 기기를 다시 들여오는 문제가 오래된 불만 | 각 기기에서 제거. introducer 설정이 있으면 재유입 |
| *(참고)* **Home Assistant 앱** | 앱 등록마다 새 기기 | 재설치하면 `_2`가 붙은 새 기기로 등록. 공식 해법은 수동 정리. 휴대폰 이름을 예전과 같게 맞춰 해결했다는 보고와, 반대로 **이름 기준으로 새 폰이 옛 폰에 합쳐진** 보고가 함께 있음 | 수동 | 통합 삭제 |
| *(참고)* **Chrome Remote Desktop** | Google 계정 + 호스트 | 재설치한 호스트가 옛 항목 "last online"으로 남는 버그 보고 | "last online" 표시 | remotedesktop.google.com 목록에서 제거 |

### 3.2 핵심 결론

1. **"재설치하면 새 기기, 옛 항목은 수동 정리"가 업계 기본값이다.** KDE Connect·Phone Link·Tailscale·Syncthing·Home Assistant 모두 그렇고, KDE Connect와 Phone Link는 이것 때문에 버그·불만이 계속 나온다. Homebase만의 결함이 아니라 설치 단위 신원(무작위 ID·인증서·푸시 엔드포인트)을 쓰는 구조의 공통 결과다.
2. **자동으로 합치는 앱은 "설치와 무관한 안정 식별자"를 따로 보낸다.** Pushbullet API의 `fingerprint`가 정확히 그 용도로 문서화돼 있다. 같은 계열의 Android 수단은 `ANDROID_ID`다: Android 8.0+에서 **앱 서명 키 × 사용자 × 기기** 조합마다 고유하고, **서명 키가 같으면 앱을 지웠다 다시 설치해도 바뀌지 않으며**, 공장 초기화나 서명 키 변경 때 바뀐다(Android 공식 문서).
3. **이름 기준 병합은 위험하다.** Home Assistant에서 이름이 같은 새 폰이 옛 폰에 합쳐진 사례가 있다. Homebase M-24가 "기기 이름으로 판단"을 기각한 판단과 일치한다. 이름·모델은 자동 병합이 아니라 "예전 기기를 대체할까요?" 물음의 근거로만 쓴다.
4. **신원은 ID 문자열이 아니라 키에 묶어야 한다.** KDE Connect의 2025년 치명 취약점(CVE-2025-66270)은 v8 핸드셰이크에서 두 패킷의 기기 ID를 대조하지 않아, 페어링된 기기의 ID만 알면 그 기기로 행세할 수 있었던 문제다. 안정 식별자는 **"이 새 항목이 그 옛 항목을 대체한다"는 정리 신호로만** 쓰고 인증은 지금처럼 페어링 키·토큰으로 해야 한다. 그래야 식별자가 새도 권한이 넘어가지 않는다.
5. **오래된 기기는 "보이게 하고 고르게" 하는 것이 대세다.** 일반 기기를 자동 삭제하는 곳은 확인되지 않았다(Tailscale은 ephemeral만 자동 삭제). 마지막 접속 시각 표시(Tailscale, CRD), 중복 배지(Tailscale), 대수 제한으로 유도하는 정리(AirDroid)가 일반적이다.
6. **해제는 한쪽만 해서는 안 끝난다.** Phone Link는 양쪽 수동 삭제를 요구하고, KDE Connect는 상대가 연결돼 있을 때만 `pair:false`가 전달된다. Homebase는 M-47 그룹 제거(`group/remove`, 7일 유지)가 이미 이보다 낫다. 다만 지워진 앱은 신호를 보낼 수 없으므로 PC 쪽에서 판단해야 한다.

---

## 4. Homebase에 넣을 기능 후보

판단 기준: 본인 PC 여러 대 + 휴대폰 한두 대를 쓰는 개인 사용자에게 실제로 쓰일 것인가. 이미 있는 기능(요청서 목록)은 제외했다. "다른 앱에 있다"만으로는 넣지 않았다.

### P1 — 지금 겪는 문제를 풀거나, 작고 매일 쓰일 것

- **재설치·교체 휴대폰 자동 대체** — 무엇: 페어링 요청에 설치와 무관한 기기 지문(`ANDROID_ID`를 그대로 보내지 않고 HMAC으로 가린 값)을 싣고, PC가 같은 지문의 옛 항목을 철회한 뒤 그룹 제거(M-47)로 다른 PC에도 퍼뜨린다. 지문이 다른데 같은 이름·모델의 휴대폰이 오래 오프라인이면 자동 병합하지 않고 "예전 ○○을 이 휴대폰으로 바꿀까요?"를 묻는다. · 왜: 지금 겪는 문제(재설치 후 옛 항목 잔존, 그 항목으로 알림·상태 요청 낭비)를 직접 해결한다. · 참고: Pushbullet `fingerprint`, 반면교사로 KDE Connect·Phone Link·Tailscale·Home Assistant. · 난이도: 중(페어링 요청 필드 하나, `MobileDeviceStore` 철회 규칙 확장, 묻는 UI) · 제약: `ANDROID_ID`는 서명 키 단위라 **GitHub판과 Play판은 값이 달라** 채널을 바꾸면 다른 기기로 보이므로 묻는 흐름이 필요하다. 공장 초기화 때 바뀐다. Play Data safety 양식의 "기기 또는 기타 ID" 항목 신고가 필요한지 검토해야 한다. 지문은 인증에 쓰지 않는다(3.2-4).
- **오래 안 보인 기기 표시·정리** — 무엇: (`lastSeenAt`은 이미 `state.json`에 저장된다 — 검토 정정) 트레이 기기 목록·휴대폰 앱 PC 목록에 "마지막 연결 3일 전"을 표시한다. N일(예: 14일) 넘게 안 보인 기기에는 배지와 "정리" 버튼을 둔다. 자동 삭제는 기본 끔, 켜면 N일 뒤 삭제. · 왜: P1-1이 못 잡는 경우(공장 초기화, 휴대폰 분실·처분, 꺼 둔 PC)를 사용자가 한눈에 정리할 수 있다. 지금은 PC를 재시작하면 오래된 기기와 방금 끊긴 기기가 구분되지 않는다. · 참고: Tailscale(마지막 접속 + 중복 배지, ephemeral만 자동 삭제), Chrome Remote Desktop "last online", Syncthing 30일 자동 삭제 요청. · 난이도: 하 · 제약: 없음(PC 쪽 저장·UI). 그룹 PC 사이에서는 각 PC가 본 시각 중 가장 최근 값을 쓰면 된다.
- **휴대폰 찾기(벨 울리기)** — 무엇: 트레이·다른 휴대폰에서 "벨 울리기"를 누르면, 무음·진동 모드여도 최대 음량 알람음을 울리고 화면에 "Homebase가 찾는 중 — 끄기"를 띄운다. · 왜: 집 안에서 휴대폰을 찾는 일은 개인 사용자에게 흔하고, 이미 있는 봉인 명령·푸시 경로로 바로 만들 수 있다. · 참고: KDE Connect "Ring My Phone"(알람음), AirDroid Find Phone, Apple 나의 찾기. · 난이도: 하~중 · 제약: 방해 금지 모드를 넘으려면 알람 오디오 용도로 재생하거나 "방해 금지 접근" 권한이 필요하다(사용자가 켜는 특수 권한, Play 허용). Android 12+ 백그라운드 포그라운드 서비스 시작 제한 때문에 푸시 수신 → 알림 채널 소리 방식이 안전하다. "완전 무음" 모드에서 울리는지는 실기기로 확인해야 한다(미확인).
- **받은 링크 바로 열기** — 무엇: 보낸 텍스트가 URL 하나면 받는 쪽에서 [열기] 버튼을 띄운다(PC는 기본 브라우저, 휴대폰은 알림 버튼). 휴대폰 공유 메뉴에서 웹페이지를 보내면 PC가 바로 열게 하는 선택도 둔다. · 왜: 휴대폰에서 보던 페이지를 PC에서 이어 보는 가장 흔한 Handoff형 용도이고, 지금은 복사 → 브라우저 붙여넣기 두 단계가 더 든다. · 참고: KDE Connect Share(URL), Pushbullet 링크 푸시, Join, Phone Link "Share webpages", Apple Handoff. · 난이도: 하(기존 클립보드·공유 경로 재사용) · 제약: Android 10+ 백그라운드 액티비티 시작 제한 때문에 휴대폰에서는 자동으로 열 수 없고 알림 버튼으로 연다. PC에서 자동으로 여는 것은 사용자가 켜는 옵션으로 둔다(피싱 링크 방지).
- **휴대폰 알림 지우기 동기화 (+ 답장은 지원 앱만)** — 무엇: PC 알림 센터·풍선에서 휴대폰발 알림을 닫으면 휴대폰의 원래 알림도 지우고, 그 반대도 한다. 원 알림에 RemoteInput 답장 동작이 있으면 PC에서 답장 칸을 연다. · 왜: 알림을 양쪽에서 두 번 치우는 일이 없어진다. 답장은 PC용 앱이 없는 메신저·SMS에서 가치가 있다(카카오톡처럼 PC 앱이 있으면 가치가 낮다). · 참고: KDE Connect(알림 답장), Pushbullet(지우기 동기화·답장), Join, Phone Link. · 난이도: 중(PC → 휴대폰 봉인 명령 2종, `NotificationListenerService.cancelNotification`·RemoteInput 호출) · 제약: 이미 있는 알림 접근 권한으로 된다. Android 15+는 OTP 같은 민감 알림 내용을 알림 리스너에 숨긴다(KDE 문서). 답장 동작은 앱마다 노출 여부가 달라 "없으면 버튼 숨김"이 필요하다. 지우기 동기화는 이번 알림 전달 작업과 자연스럽게 맞물리므로 먼저, 답장은 다음 단계로 나눌 수 있다.

### P2 — 쓰임은 분명하지만 급하지 않거나, 조건이 붙는 것

- **휴대폰 배터리 부족·충전 완료 알림(PC)** — 무엇: 휴대폰이 15% 이하·충전 완료를 보고하면 PC 트레이 토스트로 알린다(임계값 설정). · 왜: PC 앞에 있을 때 휴대폰을 덜 들여다본다. 배터리 보고는 이미 변화 시점에 올라오므로 PC 쪽 규칙만 더하면 된다. · 참고: KDE Connect 배터리 플러그인(표시), Phone Link 시작 메뉴 배터리. 알림 기능 자체는 벤치마크 앱에서 확인 못함 · 난이도: 하 · 제약: 휴대폰 보고 주기가 OEM 배터리 정책에 밀리면 늦다(M-27에 이미 적힌 대가).
- **통화가 오면 PC 미디어 일시정지** — 무엇: 휴대폰이 전화 수신·통화 시작을 감지해 연결된 PC(들)에 미디어 정지(선택: 음소거)를 보내고, 끊으면 다시 재생한다. · 왜: 헤드폰으로 PC 소리를 듣다가 전화를 놓치는 문제를 막는다. 원격 미디어 명령이 이미 있다. · 참고: KDE Connect "Pause Media During Calls", Samsung "다른 기기에서 통화". · 난이도: 중 · 제약: 통화 상태는 `READ_PHONE_STATE`(런타임 권한)로 되지만 발신자 번호는 Play가 제한하는 `READ_CALL_LOG` 영역이라 번호는 다루지 않는다(번호는 이미 전달되는 전화 앱 알림으로 충분). 휴대폰 → PC 지연은 LAN이 아닐 때 수 초일 수 있다.
- **PC에서 휴대폰 미디어 보기·제어** — 무엇: 트레이 휴대폰 상세에 지금 재생 중인 곡·앱과 재생/일시정지/다음 버튼을 둔다. · 왜: 휴대폰으로 스피커에 음악을 트는 사용자에게 편하다. 가치는 사용 패턴에 따라 갈린다. · 참고: KDE Connect 미디어 플러그인(양방향 여부 미확인), Phone Link 오디오 플레이어(미확인). · 난이도: 중 · 제약: `MediaSessionManager.getActiveSessions`는 알림 리스너 권한이 있어야 하는데 Homebase에 이미 있다. 휴대폰 → PC 상태 갱신이 푸시·presence 주기에 묶인다.
- **새 기기 페어링 시 "기기 교체" 마법사** — 무엇: P1-1의 물음을 확장해, 새 휴대폰 연결 직후 옛 휴대폰의 별칭·알림 범주·필터 설정을 넘겨받을지 묻는다. · 왜: 휴대폰을 바꿀 때 설정을 다시 하는 수고를 줄인다. 연 1회 수준이라 P2. · 참고: Phone Link(계정 단위 재연결), Tailscale(삭제 후 이름 승계 권고). · 난이도: 중 · 제약: 휴대폰 쪽 설정은 앱 안에만 있으므로(M-31·M-36) 옛 휴대폰이 살아 있을 때만 옮길 수 있다.

### P3 — 가치가 낮거나, 다른 도구가 이미 잘 하거나, 정책 비용이 큰 것

- **휴대폰을 PC 터치패드·키보드로** — 참고: KDE Connect Virtual Input, Unified Remote · 난이도: 중 · 판단: 거실 PC 같은 특수 용도. Homebase의 모니터링·알림 중심과 거리가 있다. 필요하면 Unified Remote·KDE Connect를 함께 쓰면 된다.
- **SMS 읽기·보내기(PC)** — 참고: Phone Link, KDE Connect, Pushbullet, Join, AirDroid · 판단: Play는 SMS·통화 기록 권한을 기본 핸들러나 예외 승인 앱에만 허용한다. "기기 간 문자 동기화" 예외가 있지만 사례별 심사이고 2026-07 정책 미리보기에서 요건이 바뀌는 중이다. GitHub판에만 넣으면 채널별 기능 차이가 커진다. 문자 알림 전달은 이미 된다.
- **휴대폰 파일을 PC에서 탐색** — 참고: KDE Connect SFTP, Phone Link 탐색기(Android 11+) · 판단: 전체 파일 접근(`MANAGE_EXTERNAL_STORAGE`)은 Play 심사 대상이다. 파일 보내기가 이미 있어 추가 가치가 작다.
- **새 사진 자동 보내기·최근 사진** — 참고: Phone Link 사진, Windows 새 사진 알림, Syncthing · 판단: Play 사진·동영상 권한 정책(2025-05-28 전면 시행)상 핵심 기능이 아니면 `READ_MEDIA_IMAGES`를 못 쓰고 사진 선택기를 써야 한다. 자동 백업은 Google 포토·OneDrive·Syncthing이 이미 한다.
- **휴대폰 화면 미러링·원격 제어 / PC 화면 보기** — 참고: scrcpy, Phone Link, AirDroid, RustDesk, Unified Remote Screen Viewer · 난이도: 상 · 판단: MediaProjection은 세션마다 사용자 동의가 필요하고, 무인 제어는 접근성 서비스가 필요해 Play 정책 부담이 크다. scrcpy·RustDesk가 무료로 잘 하므로 직접 만들 이유가 약하다.
- **PC → 휴대폰 클립보드 자동 반영(옵션)** — 참고: Samsung 연속성, Apple 유니버설 클립보드, ClipCascade · 판단: Homebase는 M-62에서 "누를 때 보내기"로 정했다. Android 10+는 백그라운드 클립보드 **읽기**를 막아 휴대폰 → PC 자동은 불가능하다(KDE·ClipCascade는 ADB·Shizuku 우회). PC → 휴대폰 쓰기만 자동으로 하는 비대칭 기능은 혼란 대비 가치가 낮다. 백그라운드 쓰기가 Android 버전별로 허용되는지는 **미확인**.
- **앱이 없는 기기에서 링크로 받기** — 참고: LocalSend v1.18 "Receive via link", PairDrop 공개 방 · 판단: 회사 PC·지인 휴대폰과 주고받을 때 쓸모가 있지만, 인증 없는 업로드 페이지를 여는 것이라 시간 제한·LAN 한정·일회용 코드가 필수다. 개인 기기끼리라는 Homebase 범위 밖이다.
- **PC끼리 마우스·키보드 공유** — 참고: Samsung Multi Control, KDE Connect 1.35.6(APKMirror 릴리스 노트, 공식 확인 미확인) · 판단: PowerToys Mouse Without Borders가 이미 있다.

### 넣지 않아도 되는 것(벤치마크에 있으나 Homebase에 이미 더 나은 수단이 있음)

- 페어링 확인 코드(KDE Connect 8자리 SAS — 2025년 CVE-2025-32898로 길이 문제 지적): Homebase는 QR·24단어 코드로 키를 직접 옮기므로 불필요하다.
- 같은 계정이면 자동 수락(Quick Share), 즐겨찾기 자동 수락(LocalSend): Homebase는 페어링된 기기만 보내므로 이미 그 상태다.

---

## 5. 출처

KDE Connect / GSConnect
- 프로토콜 문서(ID·인증서 CN·v8·검증 코드): https://invent.kde.org/network/kdeconnect-meta/-/raw/master/protocol.md
- Valent 프로토콜 문서: https://valent.andyholmes.ca/documentation/protocol.html
- 기기 ID 형식 강제(GSConnect 영향): https://bugs.kde.org/show_bug.cgi?id=498778
- 같은 폰 두 항목 + 해제 크래시: https://bugs.kde.org/show_bug.cgi?id=502915 , https://bugs.kde.org/show_bug.cgi?id=505587
- ID 충돌·인증서 재생성: https://bugs.kde.org/show_bug.cgi?id=387532 , https://phabricator.kde.org/R225:ea136498b491c620c9f274e8452ae8770d99ec5a
- 옛 인증서로 검증 키 계산: https://bugs.kde.org/show_bug.cgi?id=501352
- 재설치 후 기억된 기기 중복(2017): https://mail.kde.org/pipermail/kdeconnect/2017-October/002271.html
- 인증서 만료: https://invent.kde.org/network/kdeconnect-meta/-/issues/7
- 보안 권고 CVE-2025-66270(기기 사칭): https://kde.org/info/security/advisory-20251128-1.txt
- 보안 권고 CVE-2025-32900·32898: https://kde.org/info/security/advisory-20250418-2.txt , https://kde.org/info/security/advisory-20250418-3.txt
- 플러그인 목록: https://userbase.kde.org/KDEConnect
- 기능·플랫폼·릴리스: https://kdeconnect.kde.org/ , https://apps.kde.org/kdeconnect/
- Android 1.35.x 릴리스 노트(제3자 미러): https://www.apkmirror.com/apk/kde-community/kde-connect/kde-connect-1-35-10-release/
- GSConnect 버전: https://tracker.debian.org/pkg/gnome-shell-extension-gsconnect

Microsoft Phone Link
- 기기 제거: https://support.microsoft.com/en-us/topic/remove-a-device-in-the-phone-link-9382f770-b203-7e0e-c81e-18d4053cf4e5
- 중복 Linked PCs 제거 불가(Q&A): https://learn.microsoft.com/en-us/answers/questions/5825466/issue-with-duplicate-linked-pcs-and-inability-to-r
- 옛 기기 제거 불가 토론: https://techcommunity.microsoft.com/discussions/windows11/link-to-windows-does-not-allow-removal-of-old-devices/4498238
- FAQ(여러 기기): https://support.microsoft.com/en-us/Windows/apps/phonelink/frequently-asked-questions-about-the-phone-link
- 모바일 장치 관리: https://support.microsoft.com/en-US/Windows/apps/phonelink/manage-mobile-devices-in-windows
- Cross-device Resume: https://support.microsoft.com/en-us/windows/apps/phonelink/cross-device-resume-feature
- 탐색기에서 휴대폰: https://support.microsoft.com/en-us/Windows/experience/fileexplorer/setting-up-and-using-your-phone-in-file-explorer
- 교차 복사·붙여넣기: https://support.microsoft.com/help/4539429
- 시작 메뉴 휴대폰 패널: https://support.microsoft.com/windows/apps/phonelink/mobile-device-in-start-menu , https://www.androidauthority.com/phone-link-panel-windows-start-menu-3558774/

Intel Unison
- https://www.dell.com/support/kbdoc/en-us/000337603/intel-unison-end-of-service-announcement
- https://www.intel.com/content/www/us/en/support/articles/000102120/software.html

Samsung
- 앱 이어하기: https://www.samsung.com/us/support/answer/ANS10003443/
- 클립보드 공유: https://www.samsung.com/us/support/answer/ANS10003445/
- Multi Control 요건: https://www.samsung.com/uk/business/mobile/galaxy-connected-experience/

Google Quick Share
- https://support.google.com/android/answer/13801258
- https://support.google.com/product-documentation/answer/14185560

Pushbullet
- API(기기 fingerprint·active·삭제, 미러링·유니버설 복사): https://docs.pushbullet.com/
- 기기 이름 변경(Settings › Devices): https://help.pushbullet.com/articles/can-i-rename-my-devices/
- Pro 출시(2015): https://thenextweb.com/news/pushbullet-launches-a-5-pro-tier-but-thats-bad-news-for-free-users

Join
- https://joaoapps.com/join/
- 기기 ID와 백업 복원(개발자 답변): https://groups.google.com/g/tasker/c/7FTWECDL0V0
- 1회 구매·기능: https://chromewebstore.google.com/detail/join/flejfacjooompmliegamfbpjjdlhokhj

AirDroid
- 기기 추가·대수: https://help.airdroid.com/hc/articles/360003653853
- 기기 언바인드(Parental Control 앱 경로): https://help.airdroid.com/hc/en-us/articles/5658781082523
- 요금: https://www.airdroid.com/pricing/airdroid-personal/
- 기능: https://www.airdroid.com/personal/ , https://www.airdroid.com/personal/sms-notification-call-on-pc/

LocalSend / PairDrop
- https://localsend.org/changelog
- 프로토콜(지문): https://github.com/localsend/protocol
- https://github.com/schlagmichdoch/PairDrop

scrcpy
- https://newreleases.io/project/github/Genymobile/scrcpy/release/v3.3
- https://ubuntuhandbook.org/index.php/2024/12/scrcpy-3-0-virtual-display-linux-package
- https://github.com/Genymobile/scrcpy

Apple
- https://support.apple.com/102418

Unified Remote / 원격 데스크톱
- https://www.unifiedremote.com/features
- https://support.google.com/chrome/answer/1649523
- https://groups.google.com/a/chromium.org/g/chromium-bugs/c/Go6zz_od3Gs
- https://rustdesk.com/docs/en/self-host/ , https://rustdesk.com/docs/en/self-host/rustdesk-server-pro/address-book/

Syncthing / Tailscale
- https://forum.syncthing.net/t/new-feature-removal-of-devices-unseen-for-30-days/17522
- https://forum.syncthing.net/t/how-to-delete-a-device-when-everyone-is-an-introducer/10104
- https://tailscale.com/kb/1106/taildrop
- https://tailscale.com/kb/1111/ephemeral-nodes
- https://tailscale.com/docs/features/access-control/device-management/how-to/remove
- https://tailscale.com/docs/reference/troubleshooting/windows/windows-upgrade-error
- https://tailscale.com/docs/reference/troubleshooting/network-configuration/multiple-devices-same-100-x-ip-address

클립보드 동기화
- https://github.com/crosspaste/crosspaste-desktop
- https://github.com/Sathvik-Rao/ClipCascade
- Android 10+ 클립보드 제한(KDE): https://bugs.kde.org/show_bug.cgi?id=428167

Home Assistant(재설치 중복 참고)
- https://companion.home-assistant.io/docs/troubleshooting/resetting
- https://community.home-assistant.io/t/register-as-existing-mobile-device-after-reinstalling-app/669086
- https://community.home-assistant.io/t/new-phone-merges-with-old-phone/277932

Android·Play 정책
- ANDROID_ID(서명 키·사용자·기기 범위, 재설치에도 유지): https://developer.android.com/about/versions/oreo/android-8.0-changes , https://developer.android.com/reference/android/provider/Settings.Secure#ANDROID_ID
- SMS·통화 기록 권한 정책: https://support.google.com/googleplay/android-developer/answer/10208820 , https://support.google.com/googleplay/android-developer/answer/13986130
- 사진·동영상 권한 정책: https://support.google.com/googleplay/android-developer/answer/14115180
