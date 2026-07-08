## 📥 어느 파일을 받아야 하나요?

**처음 설치라면 → `LocalOpsBot-Setup.exe` 하나만 받아 더블클릭하세요.**
설치 마법사가 Telegram 봇 토큰과 chat ID를 물어보고, Windows 서비스 등록과 트레이 자동 시작까지 끝냅니다.

| 파일 | 이럴 때 받으세요 |
|---|---|
| **`LocalOpsBot-Setup.exe`** | ⭐ 대부분 이것 하나면 됩니다. 더블클릭하면 설치 마법사가 실행됩니다 |
| `LocalOpsBot-Setup.zip` | 마법사 없이 수동 설치 — 압축을 풀고 **관리자 권한** PowerShell에서 `.\setup.ps1` 실행 |
| `bootstrap.ps1` | 명령 한 줄 설치 — 관리자 권한 PowerShell에서 `irm https://github.com/jeiel85/localops-bot/releases/latest/download/bootstrap.ps1 \| iex` |
| `appsettings.example.json` | 설정 항목 참고용 (설치 시 자동 생성되므로 보통 받을 필요 없음) |
| `*.sha256` | 다운로드 무결성 검증용 (선택) |

> **사전 요구사항**: Windows 10/11 (버전 1809 이상). 설치 파일에 .NET 런타임이 포함되어 있어 별도 설치가 필요 없습니다.

---
