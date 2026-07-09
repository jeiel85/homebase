# AV-WINRING0-PROGRESS — WinRing0 오탐 대응

브랜치: `claude/competent-wilson-d686a9` (main/v0.7.4 기반). 별개 진행(i18n 브랜치와 무관).

## 목표
Windows Defender가 온도 모니터링용 WinRing0 커널 드라이버(`Homebase.Agent.sys`)를
`VulnerableDriver:WinNT/Winring0`(트로이/심각)로 탐지 → 신규 설치 첫인상 문제.

## 결정 (사용자 확정)
- 기본 센서 = **WMI/ACPI** (드라이버 없음 → 탐지 없음)
- WinRing0 전체 센서(LibreHardwareMonitor) = **opt-in**, 기본 off
- **opt-in 시 Defender 예외 자동 등록** + 해제/언인스톨 시 정리
- 설계 판단: opt-in은 elevated 헬퍼 스크립트(`enable-temperature.ps1`)로. 서비스가 런타임에
  Defender를 건드리지 않음(신뢰/감사 이유).

## 보안 주의 (문서·응답에 반영할 것)
1. Defender AV 예외(`Add-MpPreference -ExclusionPath`)는 **탐지만** 숨김.
   별도의 **취약 드라이버 차단목록(HVCI/Code Integrity/Smart App Control)** 은
   AV 예외와 무관하게 드라이버 로드를 커널에서 차단 → 그런 PC에선 opt-in해도 온도 안 나올 수 있음.
2. WMI `MSAcpi_ThermalZoneTemperature`는 많은 데스크톱에서 미구현 → 기본 상태에서 온도가
   아예 없을 수 있음(우아하게 빈 목록).

## 계약 (수정 지점 — 앵커)
- [ ] `src/LocalOpsBot.Core/Monitoring/TemperatureOptions.cs`
      `enum TemperatureSource { Wmi, LibreHardware }` (Wmi=첫 멤버=기본) + `Source` 프로퍼티.
      `Enabled`는 마스터 스위치로 유지(기존 "enabled=false" 완화책 하위호환).
- [ ] `src/LocalOpsBot.Infrastructure/Windows/WmiTemperatureCollector.cs` (신규)
      `root\WMI` → `MSAcpi_ThermalZoneTemperature.CurrentTemperature`(1/10 K) → °C = v/10 - 273.15.
      plausibility 0<c<=150. **모든 판독 Kind="Board"** (소비부가 Cpu/Gpu/Board만 표시).
      실패/미지원 → `CollectorResult.Fail` 또는 Ok(empty), 첫 실패만 warn.
- [ ] `src/LocalOpsBot.Infrastructure/ServiceCollectionExtensions.cs:84`
      `AddSingleton<ITemperatureCollector, LibreHardwareTemperatureCollector>()` →
      `Source` 기반 팩토리(LibreHardware→LHM, else→WMI). `TemperatureOptions` 싱글턴 주입.
- [ ] `config/appsettings.example.json` + `schemas/appsettings.sample.json`
      `"temperature": { "enabled": true, "source": "Wmi" }`
- [ ] `installer/enable-temperature.ps1` (신규, `#Requires -RunAsAdministrator`)
      enable: appsettings.json의 temperature.source="LibreHardware"(+enabled=true),
              `Add-MpPreference -ExclusionPath "<AgentDir>\Homebase.Agent.sys"`, Restart-Service.
      `-Disable`: source="Wmi", `Remove-MpPreference ...`, Restart-Service.
- [ ] `installer/uninstall-service.ps1` — best-effort `Remove-MpPreference` 정리 단계 추가.
- [ ] `installer/setup.iss` — enable-temperature.ps1 동봉(`Source: "enable-temperature.ps1"; ...`).
- [ ] 테스트: 옵션 바인딩(기본 Wmi / "LibreHardware" 파싱) + WMI 변환·이름 순수 함수.
- [ ] 온보딩/README 안내 한 줄 + CHANGELOG + 버전 (릴리스 확정 전 하드닝 제안과 함께 확인).

## 검증 계획
- 각 코드 변경 후 `dotnet build`.
- 완료 전 `dotnet test` 전체 green.
- PowerShell 스크립트: `Test-Path`·구문 파싱(`[scriptblock]::Create((Get-Content -Raw ...))`).
- 감사(3단계): 전체 diff ↔ 계약 1:1 대조.

## 상태 (구현 완료 · 미커밋 · 미릴리스)
- Stage 1(분석)·Stage 2(구현)·Stage 3(감사) 완료.
- 검증 근거: 빌드 경고0/오류0 · 테스트 141→**160 green**(+19) · PS 파싱 OK×2 · JSON 유효×2 ·
  Resolve-AgentDir 정규식 실측(따옴표/인자/공백 3형태) · 이 머신에 서비스 설치돼 유도 경로 실측.
- 감사에서 정확성 결함 1건 수정: `enable-temperature.ps1` InstallDir 하드코딩 → 실제 서비스
  바이너리 경로에서 Agent 폴더 유도(+커스텀 설치 경고). 기본 설치 경로는 영향 없음.
- 완료 파일: TemperatureOptions.cs / WmiTemperatureCollector.cs / Infrastructure DI 팩토리 /
  Infrastructure.csproj(InternalsVisibleTo) / config 2 / enable-temperature.ps1 / uninstall-service.ps1 /
  setup.iss / 테스트 3 / README 온도 섹션.
- 하드닝 H1~H4 전부 채택·구현: H1 /diagnostics 온도 백엔드+센서수 · H2 enable-temperature.ps1
  차단목록/HVCI 사전감지 경고(이 머신에서 True 실측 — 예외 넣어도 로드 차단됨) · H3 15_risks §8-1 ·
  H4 온보딩 카드. CHANGELOG **v0.8.0** 추가. 버전=git 태그(Directory.Build.props 0.2.0은 CI가 덮음, 미변경).
- **최종: 빌드 0/0 · 테스트 160 green · 이 브랜치에 커밋 예정(v0.8.0).**
- **남음: 태그/릴리스 배포는 사용자 확인 후. 실제 enable-temperature.ps1 라이브 실행(Defender 변경)도 승인 후.**

## 참고 사실
- 드라이버 추출 경로: LHM이 호스트 프로세스명으로 명명 → `<AgentExe폴더>\Homebase.Agent.sys`.
  기본 설치 = `C:\Program Files\Homebase\Agent\Homebase.Agent.sys` (설치 dir 가변).
- `System.Management` 9.0.0 이미 참조(csproj 주석: 0.9.5+ LHM은 System.Management 10.x/.NET10 필요 → 버전 고정 유지).
- 소비부: `StatusCommandHandler.cs:104`, `HealthThresholdEvaluator.cs:38` — `{"Cpu","Gpu","Board"}` 고정 순회.
