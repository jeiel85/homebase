# 12. Installation and Operation

## 1. 설치 전 준비

필요 항목:

- Windows 10/11
- 관리자 PowerShell
- .NET runtime 또는 self-contained publish
- Telegram 계정
- BotFather로 생성한 bot token
- 내 chat_id

## 2. 배포 방식

초기 버전은 self-contained publish를 추천한다.

장점:

- 대상 PC에 .NET runtime 설치 여부 영향 감소
- zip 배포 쉬움

예시:

```powershell
dotnet publish src/LocalOpsBot.Agent/LocalOpsBot.Agent.csproj -c Release -r win-x64 --self-contained true -o publish/Agent
dotnet publish src/LocalOpsBot.Tray/LocalOpsBot.Tray.csproj -c Release -r win-x64 --self-contained true -o publish/Tray
```

## 3. 설치 경로

```text
C:\Program Files\LocalOpsBot\Agent
C:\Program Files\LocalOpsBot\Tray
C:\ProgramData\LocalOpsBot\config
C:\ProgramData\LocalOpsBot\data
C:\ProgramData\LocalOpsBot\logs
```

## 4. install-service.ps1 기능

Script responsibilities:

1. 관리자 권한 확인
2. directory 생성
3. binaries copy
4. config example copy
5. service create
6. service recovery 설정
7. service start
8. current status 출력

Example:

```powershell
sc.exe create "LocalOpsBot.Agent" binPath= "C:\Program Files\LocalOpsBot\Agent\LocalOpsBot.Agent.exe" start= auto DisplayName= "LocalOpsBot Agent"
sc.exe description "LocalOpsBot.Agent" "Personal Windows PC monitoring bot for Telegram."
sc.exe failure "LocalOpsBot.Agent" reset= 86400 actions= restart/60000/restart/60000/restart/300000
Start-Service "LocalOpsBot.Agent"
```

## 5. Tray 자동 실행 등록

권장: Current User Startup Registry

```powershell
$path = "C:\Program Files\LocalOpsBot\Tray\LocalOpsBot.Tray.exe"
New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -Value "`"$path`"" -PropertyType String -Force
```

## 6. 최초 설정 절차

1. BotFather에서 token 생성
2. 환경변수 등록

```powershell
[Environment]::SetEnvironmentVariable("LOCALOPSBOT_TELEGRAM_TOKEN", "YOUR_TOKEN", "Machine")
```

3. 봇에게 메시지 보내기
4. 임시 스크립트나 브라우저에서 getUpdates 확인
5. `allowedChatIds` 설정
6. 서비스 재시작

```powershell
Restart-Service LocalOpsBot.Agent
```

7. `/ping` 테스트
8. `/status` 테스트

## 7. 운영 명령

```powershell
Get-Service LocalOpsBot.Agent
Start-Service LocalOpsBot.Agent
Stop-Service LocalOpsBot.Agent
Restart-Service LocalOpsBot.Agent
```

로그 확인:

```powershell
Get-Content "C:\ProgramData\LocalOpsBot\logs\agent-*.log" -Tail 100 -Wait
```

## 8. 업데이트 절차

1. 새 release zip 다운로드
2. 서비스 중지
3. 기존 binary 백업
4. 새 binary 교체
5. config 유지
6. migration 실행은 앱 시작 시 자동
7. 서비스 시작
8. `/diagnostics` 확인

PowerShell pseudo:

```powershell
Stop-Service LocalOpsBot.Agent
Copy-Item .\publish\Agent\* "C:\Program Files\LocalOpsBot\Agent" -Recurse -Force
Start-Service LocalOpsBot.Agent
```

## 9. 삭제 절차

두 가지 모드 제공.

### keep-data

- service 제거
- startup 제거
- binary 제거
- config/data/log 유지

### purge

- service 제거
- startup 제거
- binary 제거
- config/data/log 삭제

## 10. 장애 대응

| 증상 | 확인 |
|---|---|
| Telegram 응답 없음 | token, internet, log, allowedChatIds 확인 |
| `/ping` 무응답 | service 상태, polling loop 오류 확인 |
| 부팅 알림 없음 | service start type, event viewer 확인 |
| Toast 포워딩 안 됨 | Tray 실행 여부, 권한 승인 여부 확인 |
| 이벤트 로그 알림 과다 | eventLog filter와 dedup window 조정 |
| 디스크 알림 반복 | threshold와 dedup window 조정 |

## 11. 백업

백업 대상:

```text
%ProgramData%/LocalOpsBot/config/appsettings.json
%ProgramData%/LocalOpsBot/data/localops.db
```

복원:

1. 서비스 중지
2. config/db 복사
3. 서비스 시작

## 12. 설정 변경 — Hot reload (v2)

다음 설정 변경은 Agent 재시작 없이 반영된다:

- 프로세스 감시 대상 추가/제거 (`processWatches`)
- Windows Service 감시 대상 추가/제거 (`serviceWatches`)
- 알림 정책 변경 (`alerting.rateLimit`, `alerting.dedupWindowSeconds`)
- 알림 포워딩 allow/block list 변경
- 이벤트 로그 필터 변경

변경 방법: `%ProgramData%/LocalOpsBot/config/appsettings.json` 편집 후 저장. Agent가 5초 이내 감지하고 적용한다.

**변경 후에도 재시작이 필요한 설정**:

- `telegram.botToken`
- `telegram.allowedChatIds`
- `agent.databasePath`
- `logging.level` (파일 로거는 재시작 필요)

## 13. 운영 권장 설정

개인 PC 기본값:

```json
{
  "collectors": {
    "metricIntervalSeconds": 60,
    "watchIntervalSeconds": 60,
    "eventLogPollingIntervalSeconds": 30
  },
  "alerting": {
    "dedupWindowSeconds": 600,
    "maxMessagesPerMinute": 10,
    "maxMessagesPerHour": 120
  }
}
```
