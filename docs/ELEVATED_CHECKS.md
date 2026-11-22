# 승격 단계 수동 점검표

업데이트 설치와 설정 저장은 관리자 권한(UAC)이 필요한 단계를 거칩니다. 단위 테스트는 승격 스크립트의 문자열과
비승격 실행까지만 확인하고, CI 러너에서는 UAC를 띄울 수 없습니다. 그래서 **이 단계를 바꾼 릴리스**는 머지 전이나
배포 직후에 관리자 PC에서 아래를 직접 확인합니다. 바꾸지 않은 릴리스는 건너뜁니다.

해당 코드: `UpdateService.BuildApplyScript`(Core), `ElevatedConfig.BuildApplyScript`(Tray), `ProgramDataAcl`(Agent),
`installer/`. 로그: `%ProgramData%\Homebase\logs\`의 `update.log`, `agent-YYYYMMDD.log`, `tray.log`.

## 업데이트 설치

1. 트레이 › 설정 › 업데이트에서 새 버전을 설치합니다(사람이 누르는 경로). UAC 창이 한 번 뜹니다.
   - [ ] 승인하면 서비스가 멈췄다가 다시 시작되고, 트레이가 새 버전으로 다시 뜹니다.
   - [ ] `update.log`에 `Installing into <설치 폴더>`가 실제 설치 폴더로 찍히고, 오류 없이 끝납니다.
   - [ ] `%ProgramData%\Homebase_Update_*` 작업 폴더가 남지 않습니다.
2. 같은 경로에서 UAC를 **거부**합니다.
   - [ ] 아무것도 바뀌지 않고, 승인되지 않았다는 안내가 뜹니다. 서비스는 계속 돕니다.
3. 무인 설치(시작 카운트다운)를 자리를 비운 채 둡니다.
   - [ ] 승인되지 않으면 안내 모달 없이 끝나고, 다음 업데이트 창이 결과를 보여 줍니다(M-55).
4. 기본 폴더(`C:\Program Files\Homebase`)가 **아닌** 곳에 설치한 PC가 있으면 거기서 1번을 합니다.
   - [ ] 새 파일이 그 폴더에 들어가고, `C:\Program Files\Homebase`가 새로 생기지 않습니다.

## 설정 저장

트레이 설정 창에서 Agent 설정(예: 모니터 임계값, 알림 전달)을 바꾸고 저장합니다. UAC 창이 한 번 뜹니다.

- [ ] 승인하면 `%ProgramData%\Homebase\config\appsettings.json`이 바뀌고 Agent가 다시 시작됩니다.
- [ ] 저장 중에도 `appsettings.json`이 사라지는 순간이 없습니다(한 번의 이름 바꾸기로 교체). 저장 직후
      `agent-*.log`에 설정을 빈 값으로 읽었다는 경고가 없습니다.
- [ ] 새 파일의 소유자가 Administrators이고, ACL은 폴더에서 상속한 것뿐입니다
      (`Get-Acl $env:ProgramData\Homebase\config\appsettings.json | Format-List Owner, Access`).
- [ ] `config\` 폴더에 `appsettings.*.new` 임시 파일이 남지 않습니다.
- [ ] UAC를 거부하면 설정 파일이 그대로이고, 저장하지 못했다는 안내가 뜹니다.

## 데이터 폴더 권한(ACL)

- [ ] 서비스를 다시 시작해도 데이터 폴더의 ACL이 그대로입니다(이미 맞으면 다시 쓰지 않음). 다시 시작 전후
      `Get-Acl $env:ProgramData\Homebase | Format-List Access` 결과가 같고, `agent-*.log`에 ACL 오류가 없습니다.
- [ ] 일반 사용자 계정으로 `%ProgramData%\Homebase\config`에 파일을 만들 수 없습니다.

## 설치·제거

- [ ] 새로 설치(`Homebase-Setup.exe`): 서비스 등록, 트레이 시작 앱 등록, 데이터 폴더 ACL.
- [ ] 이전 버전 위에 덮어 설치: 설정·기기 목록·알림 이력이 그대로입니다.
- [ ] 제거: 서비스와 시작 앱이 빠지고, 사용자 프로필의 `.claude\settings.json`에서 Homebase 훅만 빠집니다.

## 알려진 한계

- 설치 스크립트 일부(`install-service.ps1`, `register-startup-tray.ps1`, `enable-temperature.ps1`)는 아직
  `C:\Program Files\Homebase`를 가정합니다. 다른 폴더에 설치한 PC는 업데이트는 되지만 이 스크립트를 따로 돌리면
  기본 폴더를 찾습니다.
