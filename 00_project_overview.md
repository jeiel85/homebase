# 00. Project Overview

## 1. 프로젝트 이름

작업명: **LocalOps Bot**

의미:

- Local: 내 PC 안에서 동작
- Ops: 운영/관제/상태 확인
- Bot: Telegram을 통한 대화형 인터페이스

## 2. 프로젝트 목적

개인 Windows PC의 상태, 부팅, 장애, Windows 알림, 개발 환경 프로세스 상태를 Telegram으로 확인하고 알림받는 개인용 모니터링 봇을 만든다.

## 3. 문제 정의

사용자는 PC를 항상 직접 보고 있지 않다. 특히 다음 상황에서 PC 상태를 원격으로 알고 싶다.

- PC가 정상 부팅되었는지 확인하고 싶다.
- 개발 서버, 로컬 LLM, DB, Docker, Node 서버가 죽었는지 알고 싶다.
- 디스크 용량이 갑자기 부족해지는지 알고 싶다.
- Windows 오류 로그가 발생했는지 알고 싶다.
- 카카오톡, 브라우저, 시스템 알림 등 Windows Toast 알림을 휴대폰 Telegram으로 받고 싶다.
- 외부 접속 포트를 열거나 별도 클라우드 서버를 두고 싶지는 않다.

## 4. 목표 범위

### 4.1 포함 범위

- Telegram Bot API 연동
- Telegram command polling
- boot notification
- heartbeat/status command
- CPU/RAM/disk/network summary
- uptime report
- process watch
- Windows service watch
- Windows Event Log watch
- Windows Toast notification forwarding
- notification filtering/masking
- local SQLite log
- local configuration
- PowerShell installer
- service recovery setting

### 4.2 제외 범위

초기 버전에서는 다음 기능을 제외한다.

- 임의 shell command 원격 실행
- keylogger류 입력 감시
- clipboard 전체 감시
- 브라우저 방문 기록 감시
- 메신저 본문을 앱 내부에서 직접 훔쳐보는 방식
- 외부 inbound server/webhook
- 여러 사용자 계정용 SaaS 서버
- 공개 배포용 중앙 서버
- 원격 데스크톱 기능
- 보안 제품 우회 기능

## 5. 핵심 판단

### 5.1 Telegram webhook 대신 polling

개인 PC 앱은 외부에서 접근 가능한 HTTPS endpoint를 운영하기 어렵고, 포트 개방은 보안 리스크가 크다. 따라서 `getUpdates` 기반 long polling을 기본값으로 한다.

### 5.2 Windows Service와 Tray App 분리

Windows Service는 시스템 세션에서 동작하며 사용자 UI/알림 세션과 분리된다. 따라서 시스템 감시는 Service가 담당하고, 사용자 알림 포워딩은 Tray App이 담당한다.

### 5.3 “모든 것”의 정의

이 프로젝트에서 “모든 것 모니터링”은 OS가 합법적/정상적으로 제공하는 관측 지점에 한정한다.

| 영역 | 허용 |
|---|---|
| 시스템 리소스 | 허용 |
| 이벤트 로그 | 허용 |
| 프로세스 목록 | 허용 |
| 서비스 상태 | 허용 |
| Windows Toast 알림 | 사용자 권한 허용 후 가능 |
| 키 입력 | 제외 |
| 비밀번호/인증 정보 수집 | 제외 |
| 우회/은닉 수집 | 제외 |

## 6. 최종 사용자 경험

예시:

```text
사용자: /status
봇: 🖥 DESKTOP-01
    Uptime: 3d 04h 12m
    CPU: 14%
    RAM: 11.2 / 31.8 GB (35%)
    Disk C: 86.4 GB free / 476.2 GB
    Network: Online
    Watched: PostgreSQL OK, Ollama OK, Node API DOWN
```

```text
자동 알림:
🟢 DESKTOP-01 부팅 완료
Time: 2026-07-07 17:22:11 KST
User: jeiel
IP: 192.168.0.20
```

```text
자동 알림:
🔔 Chrome
큰사랑교회 홈페이지 배포 완료
```

## 7. 성공 기준

- PC 재부팅 후 60초 이내 부팅 알림 발송
- `/ping` 명령 응답 성공률 99% 이상
- `/status` 명령 응답 시간 3초 이내
- Telegram token 유출 없이 운영 가능
- chat_id allowlist 외 사용자 명령 무시
- 알림 중복 폭주 방지
- Tray App이 죽어도 Agent는 계속 동작
- Agent가 죽으면 Windows Service Recovery가 재시작
