<p align="center">
  <img src="assets/banner.svg" width="900" alt="Homebase">
</p>

<p align="center">
  <a href="https://github.com/jeiel85/localops-bot/actions"><img src="https://img.shields.io/github/actions/workflow/status/jeiel85/localops-bot/release.yml?style=flat-square&logo=github&label=release" alt="Release"></a>
  <a href="https://github.com/jeiel85/localops-bot/blob/main/LICENSE"><img src="https://img.shields.io/github/license/jeiel85/localops-bot?style=flat-square" alt="License"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet" alt=".NET"></a>
  <a href="https://github.com/jeiel85/localops-bot/issues"><img src="https://img.shields.io/github/issues-raw/jeiel85/localops-bot?style=flat-square&logo=github" alt="Issues"></a>
</p>

<p align="center">
  <b>Homebase</b> &mdash; 개인 Windows PC 상태, 부팅, 장애, 알림을 Telegram으로 확인하고 알림받는 개인용 모니터링 봇입니다.<br>
  외부 포트를 열지 않고, outbound HTTPS만으로 동작합니다.
</p>

<p align="center">
  <a href="#-기능">기능</a> •
  <a href="#-시작하기">시작하기</a> •
  <a href="#-명령어">명령어</a> •
  <a href="#-아키텍처">아키텍처</a> •
  <a href="#-기술-스택">기술 스택</a> •
  <a href="#-프로젝트-구조">프로젝트 구조</a>
</p>

---

## ✨ 기능

| | 기능 | 설명 |
|---|---|---|
| 🖥️ | **시스템 상태** | CPU, RAM, 디스크, 네트워크, 가동 시간을 Telegram으로 확인 |
| 🚀 | **부팅 알림** | PC가 부팅되면 Telegram으로 즉시 알림 |
| 👁️ | **프로세스/서비스 감시** | Ollama, PostgreSQL, 개발 서버 등의 상태 모니터링 |
| 📋 | **이벤트 로그 감시** | Windows 오류/심각 이벤트를 Telegram으로 전달 |
| 🔔 | **토스트 알림 전달** | Windows 알림을 프라이버시 필터링과 함께 전달 |
| 🔒 | **프라이버시 우선** | 비밀번호, OTP, 토큰 마스킹; 민감 앱 차단 |
| 🔇 | **조용한 시간** | `/mute 1h`로 일시적으로 알림 음소거 |
| 📊 | **SQLite 로깅** | 명령 및 알림 이력 영구 저장 |

## 🚀 시작하기

### 사전 요구사항

- Windows 10/11 (버전 1809 이상)
- Telegram 계정 + [BotFather](https://t.me/BotFather) 봇 토큰
- 설치 파일에 .NET 런타임이 포함되어 있어 **별도 런타임 설치는 필요 없습니다**

### 설치 — 어느 파일을 받나요?

[**최신 릴리스**](https://github.com/jeiel85/localops-bot/releases/latest)에서 상황에 맞는 파일 하나만 받으세요.

| 파일 | 이럴 때 받으세요 |
|---|---|
| **`Homebase-Setup.exe`** | ⭐ **대부분 이것 하나면 됩니다.** 더블클릭하면 마법사가 토큰·chat ID를 묻고 설치를 끝냅니다 |
| `Homebase-Setup.zip` | 마법사 없이 수동 설치. 압축을 풀고 **관리자** PowerShell에서 `.\setup.ps1` 실행 |
| `bootstrap.ps1` | 명령 한 줄 설치 (아래 방법 B) |

**방법 A — 설치 마법사 (권장)**

1. `Homebase-Setup.exe`를 받아 더블클릭
2. **Windows SmartScreen** 경고("Windows의 PC 보호")가 뜨면 → **추가 정보** → **실행**을 누르세요. 서명되지 않은 개인용 앱이라 표시되는 정상 경고입니다
3. 마법사가 물어보는 Telegram 봇 토큰과 chat ID 입력
4. 설치가 끝나면 Telegram에서 봇에게 `/ping` 전송

**방법 B — 한 줄 설치**

**관리자 권한** PowerShell에서:

```powershell
irm https://github.com/jeiel85/localops-bot/releases/latest/download/bootstrap.ps1 | iex
```

> **chat ID를 모르나요?** 봇에게 아무 메시지나 보낸 뒤 `https://api.telegram.org/bot<봇토큰>/getUpdates`를 열어 `chat.id` 값을 확인하세요.

### 직접 빌드

[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)가 필요합니다.

```powershell
dotnet build LocalOpsBot.sln
```

## 🤖 명령어

| 명령어 | 설명 |
|---|---|
| `/ping` | 봇 상태 확인 |
| `/help` | 사용 가능한 명령어 표시 |
| `/status` | 전체 PC 상태 (CPU, RAM, 디스크, 네트워크) |
| `/uptime` | 시스템 가동 시간 |
| `/disk` | 드라이브별 디스크 사용량 |
| `/process` | 감시 중인 프로세스 상태 |
| `/services` | Windows 서비스 상태 |
| `/events` | 최근 Windows 이벤트 로그 |
| `/alerts` | 최근 알림 이력 |
| `/mute 1h` | 일정 시간 동안 알림 음소거 |
| `/unmute` | 알림 다시 활성화 |
| `/diagnostics` | 에이전트 자체 진단 |

## 🏗️ 아키텍처

```
┌─────────────────────────────────────────────┐
│ Telegram 모바일/데스크톱                     │
└───────────────────┬─────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────┐
│ Telegram Bot API (outbound HTTPS, polling)   │
└───────────────────┬─────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────┐
│ 로컬 Windows PC                              │
│                                               │
│  ┌─────────────────────────────────────────┐ │
│  │ LocalOpsBot.Agent (Windows 서비스)       │ │
│  │ • 부팅 알림                             │ │
│  │ • 상태 수집기                           │ │
│  │ • 이벤트 로그 감시                      │ │
│  │ • 프로세스/서비스 감시                  │ │
│  │ • Telegram 폴링                         │ │
│  └───────────────────┬─────────────────────┘ │
│                      │ Named Pipe IPC        │
│  ┌───────────────────▼─────────────────────┐ │
│  │ LocalOpsBot.Tray (사용자 세션 앱)        │ │
│  │ • 토스트 알림 리스너                    │ │
│  │ • 알림 필터/마스킹                      │ │
│  │ • 로컬 설정 UI                          │ │
│  └─────────────────────────────────────────┘ │
│                                               │
│  ┌─────────────────────────────────────────┐ │
│  │ LocalOpsBot.Data (SQLite)               │ │
│  └─────────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
```

## 🛠️ 기술 스택

<p>
  <img src="https://img.shields.io/badge/.NET_9-512BD4?logo=dotnet&logoColor=white" alt=".NET 9">
  <img src="https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white" alt="C#">
  <img src="https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white" alt="SQLite">
  <img src="https://img.shields.io/badge/WPF-0C54C2?logo=windows&logoColor=white" alt="WPF">
  <img src="https://img.shields.io/badge/Telegram_Bot_API-26A5E4?logo=telegram&logoColor=white" alt="Telegram Bot API">
  <img src="https://img.shields.io/badge/xUnit-5E5E5E?logo=xunit&logoColor=white" alt="xUnit">
  <img src="https://img.shields.io/badge/Serilog-2C3E50?logo=serilog&logoColor=white" alt="Serilog">
</p>

| 구성요소 | 기술 |
|---|---|
| 런타임 | .NET 9 |
| 언어 | C# 12 |
| IPC | Named Pipes (length-prefixed JSON) |
| 데이터베이스 | SQLite via Microsoft.Data.Sqlite |
| UI (트레이) | WPF |
| 봇 프로토콜 | Telegram Bot API (long polling) |
| DI/호스팅 | Microsoft.Extensions.Hosting |
| 로깅 | Serilog |
| 테스팅 | xUnit |

## 📁 프로젝트 구조

```
LocalOpsBot/
├─ src/
│  ├─ LocalOpsBot.Core/           # 도메인 모델, 인터페이스
│  ├─ LocalOpsBot.Infrastructure/ # Telegram, Windows 수집기
│  ├─ LocalOpsBot.Data/           # SQLite 영속성
│  ├─ LocalOpsBot.Agent/          # Windows 서비스 진입점
│  └─ LocalOpsBot.Tray/           # WPF 트레이 앱
├─ tests/
│  └─ LocalOpsBot.Tests/          # 단위 및 통합 테스트
├─ installer/                     # PowerShell 스크립트
├─ config/                        # 설정 예시
├─ docs/                          # GitHub Pages 랜딩 페이지
└─ assets/                        # 이미지, 배너
```

## 📄 라이선스

MIT &mdash; 자세한 내용은 [LICENSE](LICENSE) 파일을 참고하세요.

---

<p align="center">
  <sub>.NET과 ❤️로 만들었습니다</sub>
</p>
