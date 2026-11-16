<p align="center">
  <img src="assets/banner.svg" width="900" alt="Homebase">
</p>

<p align="center">
  <a href="https://github.com/jeiel85/homebase/actions"><img src="https://img.shields.io/github/actions/workflow/status/jeiel85/homebase/release.yml?style=flat-square&logo=github&label=release" alt="Release"></a>
  <a href="https://github.com/jeiel85/homebase/blob/main/LICENSE"><img src="https://img.shields.io/github/license/jeiel85/homebase?style=flat-square" alt="License"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet" alt=".NET"></a>
  <a href="https://github.com/jeiel85/homebase/issues"><img src="https://img.shields.io/github/issues-raw/jeiel85/homebase?style=flat-square&logo=github" alt="Issues"></a>
</p>

<p align="center">
  <b>Homebase</b> &mdash; 내 Windows PC 여러 대를 Android 앱에서 실시간으로 보고, 경보·부팅·Windows 알림을 종단 암호화 푸시로 받는 개인용 모니터링입니다.<br>
  PC·휴대폰끼리 서로의 상태를 보고 알림을 주고받습니다. Telegram 봇은 전환 기간의 대체 채널입니다.<br>
  인터넷에 포트를 열지 않습니다. 휴대폰 앱은 같은 LAN(TLS)·Tailscale 망·Cloudflare Tunnel(outbound) 중 되는 경로로 연결하고, 푸시·Telegram은 outbound HTTPS입니다.
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
| 🔔 | **토스트 알림 전달** | Windows 알림을 프라이버시 필터링·앱별 묶음과 함께 휴대폰 앱으로 암호화 전달(기본 켜짐) |
| 📲 | **앱 푸시 알림** | 경보·부팅·Windows 알림을 Homebase 앱으로 UnifiedPush 종단 암호화(RFC 8291) 전송, 실패 시에만 Telegram |
| 🔒 | **프라이버시 우선** | 비밀번호, OTP, 토큰 마스킹; 민감 앱 차단 |
| 🔇 | **조용한 시간** | `/mute 1h`로 일시적으로 알림 음소거 |
| 📊 | **SQLite 로깅** | 명령 및 알림 이력 영구 저장 |
| 📱 | **휴대폰 실시간 모니터링** | 여러 PC를 QR로 등록하고, 열린 창·창별 CPU/RAM/I/O/네트워크, GPU·드라이브·온도·인터넷 핑, Claude Code 세션을 실시간으로 확인 (Android, 아래 참고) |

## 🚀 시작하기

### 사전 요구사항

- Windows 10/11 (버전 1809 이상)
- Telegram 계정 + [BotFather](https://t.me/BotFather) 봇 토큰
- 설치 파일에 .NET 런타임이 포함되어 있어 **별도 런타임 설치는 필요 없습니다**

### 설치 — 어느 파일을 받나요?

[**최신 릴리스**](https://github.com/jeiel85/homebase/releases/latest)에서 상황에 맞는 파일 하나만 받으세요.

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
irm https://github.com/jeiel85/homebase/releases/latest/download/bootstrap.ps1 | iex
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

## 📱 휴대폰 앱 (Homebase Mobile)

PC에서 에이전트로 돌리는 작업을 휴대폰에서 실시간으로 지켜봅니다. 집·회사·노트북 PC를 여러 대 등록하고 목록에서 골라 들어가면
그 PC의 **열린 창 목록**(Chrome은 창마다 활성 탭 제목), **창(프로세스)별 CPU·RAM·I/O·네트워크**, **GPU(부하·VRAM·온도·전력)·드라이브 여유 공간·CPU 온도·인터넷 핑**, **최근 활동한 Claude Code 세션 제목**이 1초마다 갱신됩니다.

- **연결 (Tailscale은 선택)**: QR 한 장에 PC로 가는 경로가 모두 들어 있고, 앱이 휴대폰의 현재 네트워크를 보고 알아서 고릅니다.
  1. **같은 Wi-Fi·유선(LAN)**: 가장 빠릅니다. PC의 사설 주소로 TLS 연결하며, 앱은 QR에 담긴 PC 인증서 지문만 신뢰합니다.
     Windows가 그 네트워크를 **개인(Private)** 으로 분류해야 열립니다(공용이면 Connect Phone… 창이 알려 줍니다).
  2. **[Tailscale](https://tailscale.com/)**: PC와 휴대폰이 같은 tailnet에 있으면 어디서든 직접 연결합니다.
  3. **Cloudflare Tunnel**: Tailscale 없이 밖에서 볼 때 씁니다. PC가 Cloudflare로 outbound 연결만 하므로 공유기 포트를 열지 않습니다.
     토큰을 넣지 않으면 계정 없는 Quick Tunnel(임시 주소, PC 재시작마다 바뀜 — 앱이 LAN/Tailscale로 한 번 붙으면 새 주소를 자동으로 배움)을,
     Cloudflare 대시보드에서 만든 터널의 토큰과 공개 호스트명을 넣으면 고정 주소를 씁니다. `cloudflared`가 필요하며 창에서 바로 설치할 수 있습니다.
  어느 경로든 인증은 같은 페어링 토큰(Bearer)이고, 앱 상세 화면 위쪽에 지금 붙은 경로(LAN/Tailscale/Cloudflare)가 표시됩니다.
- **가벼움**: 휴대폰이 상세 화면을 보고 있을 때만 수집합니다. 보지 않을 때 추가 부하는 사실상 0입니다.
  - GPU는 Windows가 주는 값(작업 관리자와 같은 출처)으로 읽어 제조사와 상관없고 커널 드라이버가 필요 없습니다. NVIDIA 카드는 보는 동안 `nvidia-smi`로 전력·클럭을 더 읽는데, 노트북에서는 그동안 외장 GPU가 절전에 들지 못합니다. 설정 파일의 `mobile.nvidiaSmi`를 `false`로 하면 `nvidia-smi`를 쓰지 않습니다(그 카드도 Windows가 주는 값만 보입니다).
  - 인터넷 핑은 보는 동안 2초마다 `1.1.1.1`로 보냅니다. 설정 파일의 `mobile.pingHost`로 바꾸거나 `""`로 끌 수 있습니다.
- **설치**: [최신 릴리스](https://github.com/jeiel85/homebase/releases/latest)의 `Homebase-Mobile.apk`를 휴대폰에서 받아 설치합니다. 이후 업데이트는 앱이 하루 한 번 확인해 알려 줍니다.
  Google Play판도 준비 중입니다(현재 테스트 트랙). Play판은 Play 스토어가 업데이트하고 앱 안 업데이트 확인이 없습니다.
  **두 판은 서명이 달라 서로 덮어 설치할 수 없습니다** — 바꾸려면 앱을 지우고 다른 판을 설치한 뒤 PC를 다시 연결하세요([채널별 빌드](docs/PLAY_RELEASE.md)).
- **연결하기**: PC 트레이 아이콘 우클릭 → **설정…** → **휴대폰** 탭(영어 Windows: Settings… → Phone) → 쓸 경로를 고르고(기본: 전부) ENABLE과 관리자 승인 → 표시된 QR을 앱의 **기기 추가**로 스캔합니다.
  앱이 아직 없다면 휴대폰 **기본 카메라**로 QR을 찍으세요. 설치 안내 페이지가 열리고, 설치 후 그 페이지의 **앱에서 열기**로 바로 연결이 이어집니다.
  창의 CONNECTION PATHS에서 경로별 상태를 보고, 빠진 구성 요소(Tailscale·cloudflared)는 INSTALL(winget)로 설치합니다.
  QR은 5분 동안 한 번만 쓸 수 있고, 같은 창에서 연결된 휴대폰을 해제할 수 있습니다.
- **푸시 알림 (UnifiedPush)**: PC 경보(프로세스·서비스·HTTP·포트 다운, 이벤트 로그, AI 조언), 부팅 알림, Windows 알림이 앱 알림으로 옵니다.
  휴대폰에 UnifiedPush 배포자 앱 하나가 필요합니다. [ntfy](https://ntfy.sh/)(무료, 계정 없음)를 Play 스토어나 F-Droid에서 설치하면
  Homebase 앱이 처음 PC를 연결할 때 알아서 켭니다(앱 **설정**의 **푸시 알림** 스위치).
  - **Google Play 서비스가 없는 휴대폰은 F-Droid판 ntfy**를 씁니다. F-Droid판은 ntfy 서버와 상시 연결을 하나 유지하고, 상시 알림을 하나 띄웁니다.
    휴대폰이 절전(Doze) 중일 때도 이 연결을 유지하려면 **ntfy의 배터리 최적화 예외**를 켜야 합니다(설정 → 앱 → ntfy → 배터리 → 제한 없음).
    켜지 않으면 절전 중 알림이 유지보수 창까지 늦어집니다. Homebase 앱이 이 상태를 감지해 푸시 알림 행에 안내합니다.
    Play판 ntfy는 ntfy.sh 메시지를 FCM으로 깨워 받으므로 이 설정이 필요 없습니다. 내용은 PC에서 휴대폰 키로 암호화(RFC 8291)되어
  ntfy 서버는 암호문만 봅니다. 알림 채널이 PC·앱별로 나뉘므로 Android 알림 설정에서 원하는 것만 끌 수 있습니다.
  - 전환 기간에는 Telegram이 대체 채널입니다. 푸시를 등록한 휴대폰이 없거나 전송이 실패한 경보만 Telegram으로 갑니다.
    **Windows 알림은 Telegram으로 보내지 않습니다**(평문 클라우드). 예전처럼 받으려면 `notificationForwarding.telegramFallback: true`.
  - Windows 알림 포워딩은 이제 기본 켜짐이며 1Password·Bitwarden·Authy 등은 항상 차단, 6자리 코드·비밀번호·토큰은 마스킹합니다.
    같은 앱의 알림은 `bundleSeconds`(기본 60초) 동안 한 건으로 묶고, 하루 `maxPushesPerDay`(기본 150건)를 넘으면 버립니다.
    ntfy.sh 익명 한도가 휴대폰(구독자) IP당 하루 250건이고 모든 PC가 이를 나눠 쓰기 때문입니다(2026-11-13 확인). PC가 여러 대면 이 값을 줄이세요. 설정 파일에 `"enabled": false`가 이미 적혀 있으면 그대로 꺼져 있습니다.
- **알림 센터**: 앱 첫 화면 위쪽 종 아이콘(안 읽은 개수 표시)에서 받은 푸시 알림 이력을 날짜별로 봅니다. 종류·PC로 거르고,
  눌러서 전문·PC 발송 시각을 보거나 그 PC를 엽니다. Android 알림 창에서 지웠거나 알림 권한이 꺼져 있던 알림도 남습니다.
  이력은 최근 30일·최대 500건을 그 휴대폰에만 암호화해 보관하며(PC로 보내지 않음), 휴지통 아이콘으로 모두 지울 수 있습니다.
  같은 출처(앱·감시 종류)의 알림은 하루 단위로 한 묶음 카드로 접히고, 누르면 펼쳐집니다.
- **알림 우선순위**: 앱 설정 → 알림 → "알림 우선순위"에서 종류별(Windows 앱 알림·감시 경보·다른 기기 알림…)·앱별로
  긴급(팝업+소리+진동) / 보통(소리+진동) / 조용히(무음) / 센터에만(알림 창에 안 띄움) 중에 고르거나 팝업·소리·진동을 따로 켜고 끕니다.
  "같은 출처끼리 묶기"를 켜 두면(기본) 같은 앱의 알림이 Android 알림 창에서 한 묶음으로 접혀(출처별 최근 8건) 목록이 길어지지 않습니다.
  기본값은 앱 알림(Windows·다른 기기)은 보통, PC 경보는 긴급입니다.
- **PC 배지**: PC마다 색과 이름 첫 글자로 된 둥근 배지가 정해져, 그 PC의 알림 오른쪽·PC 목록 카드·알림 센터에 같은 모양으로 붙습니다.
  알림 머리줄은 "작업실 · 카카오톡"처럼 보낸 PC(다른 기기 알림은 그 기기)부터, 굵은 제목은 알림 내용만 보여 줍니다.
- **PC에서 기기 보기 (트레이 대시보드)**: 트레이 아이콘을 누르면 왼쪽에 이 PC·연결한 PC·휴대폰 목록이, 오른쪽에 고른 기기의 상세가 나옵니다.
  PC는 휴대폰 앱의 PC 화면과 같은 수준(CPU·메모리·GPU 게이지와 60초 추이, 디스크 I/O·네트워크·핑·온도, 드라이브, 앱·프로세스·작업 목록)을
  실시간으로 보여 줍니다. 어느 쪽에서 연결했든 두 PC 모두 서로를 볼 수 있고(상대 PC도 v0.33.0 이상), 창을 숨기면 10초 안에 수집이 멈춥니다.
  휴대폰은 마지막 보고(배터리·온도·충전 방식, 메모리·저장공간, 네트워크·신호, Android 버전, 절전·발열·방해 금지, 알림 전달, 이 PC 푸시 등록 여부)를 보여 줍니다.
  휴대폰은 앱이 열려 있으면 약 1분마다, 닫혀 있으면 약 30분마다 보고하므로 실시간이 아닙니다.
- **PC 알림 센터**: 트레이 메뉴 "알림 센터"(안 읽은 개수 표시), 대시보드 위쪽 버튼, 다른 기기 알림 풍선을 누르면 열립니다.
  휴대폰이나 연결한 PC가 이 PC로 보낸 알림과 이 PC의 경보(이벤트 로그·감시 대상·개발 서버)를 날짜별로 보여 주고, 출처별로 거를 수 있습니다.
  Agent가 30일(중계 알림은 최대 500건) 보관하며, 이 PC 자신의 Windows 알림은 Windows 알림 센터에 있으므로 넣지 않습니다.
- 프로세스별 네트워크는 Agent가 관리자 권한(서비스)으로 돌 때만 집계되고, Claude 세션은 제목·폴더·시간만 읽습니다(대화 내용은 읽지 않음).
  설정의 `mobile.claudeSessions: false`로 끌 수 있습니다. 프로토콜은 [`docs/MOBILE_PROTOCOL.md`](docs/MOBILE_PROTOCOL.md)를 참고하세요.

## 🌡️ 온도 모니터링

`/status`와 조언(`/advise`)에 CPU·보드 온도가 함께 표시됩니다. 센서 백엔드는 두 가지입니다.

- **기본값 — WMI/ACPI 센서:** 커널 드라이버를 로드하지 않아 백신에 탐지되지 않습니다. 다만 메인보드 ACPI 존 온도만 제공하며, **많은 데스크톱은 이 값을 노출하지 않아** 온도가 표시되지 않을 수 있습니다.
- **정밀 센서(opt-in) — LibreHardwareMonitor:** CPU 코어별·GPU·보드 온도까지 읽지만, 이를 위해 **WinRing0 커널 드라이버**를 로드합니다. Windows Defender는 이 드라이버를 취약 드라이버(`VulnerableDriver:WinNT/Winring0`)로 분류합니다.

정밀 센서를 켜려면 **관리자 PowerShell**에서:

```powershell
& "C:\Program Files\Homebase\enable-temperature.ps1"
```

이 스크립트는 설정을 `LibreHardware`로 바꾸고, 드라이버에 대한 **Defender 예외를 자동 등록**한 뒤 서비스를 재시작합니다. 되돌리려면 `-Disable`을 붙여 실행하세요.

> **보안 주의:** Defender 예외는 백신 *탐지*만 숨깁니다. 이 PC가 취약 드라이버 차단목록(메모리 무결성/HVCI/Smart App Control — 최신 Windows 11에서 기본 켜짐)을 적용 중이면, 예외와 무관하게 드라이버 로드가 커널 수준에서 차단되어 온도가 여전히 비어 있을 수 있습니다. 그 경우 기본 WMI 센서를 유지하세요.

## 🏗️ 아키텍처

```
┌─ Homebase 앱 (Android)
├─ 다른 PC (Homebase)
│     LAN(TLS) · Tailscale · Cloudflare Tunnel(outbound) — PC가 포트를 열지 않음
▼
로컬 Windows PC
├─ LocalOpsBot.Agent (Windows 서비스)
│     • 상태 수집 · 프로세스/서비스/이벤트 감시
│     • 모바일 API · 페어링 · 기기 허브(M-27)
│     • 알림 중계 · 암호화 푸시 전송
│     • Telegram 폴링(대체 채널)
│        ▲
│        │ Named Pipe IPC
│        ▼
├─ LocalOpsBot.Tray (사용자 세션 앱)
│     • Windows 알림 수집 · 필터/마스킹
│     • 대시보드 · 설정 · 알림 센터
├─ LocalOpsBot.Data (SQLite)
│
└─ outbound HTTPS만
      → UnifiedPush(ntfy) → 휴대폰 알림
      → Telegram Bot API (대체 채널)
```

## 🛠️ 기술 스택

<p>
  <img src="https://img.shields.io/badge/.NET_9-512BD4?logo=dotnet&logoColor=white" alt=".NET 9">
  <img src="https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white" alt="C#">
  <img src="https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white" alt="SQLite">
  <img src="https://img.shields.io/badge/WPF-0C54C2?logo=windows&logoColor=white" alt="WPF">
  <img src="https://img.shields.io/badge/Kotlin-7F52FF?logo=kotlin&logoColor=white" alt="Kotlin">
  <img src="https://img.shields.io/badge/Jetpack_Compose-4285F4?logo=jetpackcompose&logoColor=white" alt="Jetpack Compose">
  <img src="https://img.shields.io/badge/UnifiedPush-1E88E5" alt="UnifiedPush">
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
| 휴대폰 앱 | Kotlin · Jetpack Compose (Android 8.0+, target API 36) |
| 푸시 | UnifiedPush + RFC 8291 메시지 암호화 (의존성 없이 플랫폼 암호) |
| 봇 프로토콜 | Telegram Bot API (long polling, 대체 채널) |
| DI/호스팅 | Microsoft.Extensions.Hosting |
| 로깅 | Serilog |
| 테스팅 | xUnit |

## 📁 프로젝트 구조

```
homebase/
├─ src/
│  ├─ LocalOpsBot.Core/           # 도메인 모델, 인터페이스
│  ├─ LocalOpsBot.Protocol/       # 기기 간 메시지 형식(Device Hub)
│  ├─ LocalOpsBot.Infrastructure/ # Telegram, 푸시, Windows 수집기
│  ├─ LocalOpsBot.Data/           # SQLite 영속성
│  ├─ LocalOpsBot.Agent/          # Windows 서비스 진입점, 모바일 API
│  └─ LocalOpsBot.Tray/           # WPF 트레이 앱
├─ mobile/                        # Android 앱 (Kotlin/Compose, github·play 채널)
├─ tests/
│  └─ LocalOpsBot.Tests/          # 단위 및 통합 테스트
├─ installer/                     # PowerShell 스크립트
├─ config/                        # 설정 예시
├─ docs/                          # GitHub Pages 랜딩·페어링 안내 페이지, 설계 문서
└─ assets/                        # 이미지, 배너
```

## 📄 라이선스

MIT &mdash; 자세한 내용은 [LICENSE](LICENSE) 파일을 참고하세요. 앱·PC 프로그램의 정보 처리는 [개인정보처리방침](https://jeiel85.github.io/homebase/privacy/)에 있습니다.

---

<p align="center">
  <sub>.NET과 ❤️로 만들었습니다</sub>
</p>
