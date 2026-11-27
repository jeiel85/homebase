# Google Play 빌드 내보내기

Homebase 앱은 배포 채널마다 따로 빌드합니다(DECISIONS M-33).

| 채널 | Gradle flavor | 산출물 | 서명 | 업데이트 |
|---|---|---|---|---|
| GitHub Releases | `github` | `Homebase-Mobile.apk` (CI `release.yml`) | 릴리스 키 `homebase-release.jks` (M-12) | 앱 안 업데이터(하루 한 번 확인) |
| Google Play | `play` | `app-play-release.aab` (CI `play-release.yml`, M-79) | Play 업로드 키 `homebase-play-upload.jks` → Play가 앱 서명 키로 다시 서명 | Play 스토어가 설치. 앱은 Play In-App Updates API로 새 버전이 있는지만 확인해 알리고 스토어 페이지를 연다(M-57) |

두 채널은 패키지 이름(`io.github.jeiel85.homebase`)과 versionCode 규칙(M-4)이 같지만 서명이 달라 **서로 덮어 설치할 수 없습니다.**
채널을 바꾸려면 앱을 지우고 다시 설치한 뒤 PC를 다시 연결해야 합니다.

## CI로 올리기 (기본, DECISIONS M-79)

릴리스 태그 `vX.Y.Z`를 올리면 `release.yml`(GitHub 릴리스)과 함께 **`play-release.yml`**이 돕니다.

1. 실행이 GitHub 환경 `play`의 승인을 기다립니다. Actions → Play release → 그 실행 → **Review deployments**에서
   jeiel85가 승인합니다. 승인 전에는 아무것도 올라가지 않습니다(30일 지나면 취소됨).
2. 승인되면 Play 단위 테스트 → `bundlePlayRelease` → 번들 확인(versionName, 자체 업데이터 없음, 디버그 기호) →
   업로드 키 인증서 SHA-256 고정 비교 → **내부 테스트**에 업로드 → 같은 versionCode를 **비공개 테스트(alpha)**에
   붙임(다시 업로드하지 않음) → 트랙 상태 출력. alpha는 매번 Google 검토를 거칩니다. production은 올리지 않습니다.
3. 출시 노트: en-US는 CHANGELOG의 그 버전 절에서 항목마다 굵은 제목 한 줄씩, ko-KR은
   `mobile/play-notes/X.Y.Z.ko.txt`가 있으면 그 내용(없으면 영어 줄 그대로). 언어마다 500자까지 자릅니다.
   한국어 노트를 쓰려면 릴리스 PR에 그 파일을 같이 넣습니다.
4. 결과는 실행 로그의 "Play tracks (after)"와, 로컬에서 `pwsh ~/.claude/skills/play-publish/play-publish.ps1
   -PackageName io.github.jeiel85.homebase -ShowTracks`로 확인합니다. AAB와 노트는 실행의 아티팩트로 30일 남습니다.

손으로 돌리기: Actions → Play release → Run workflow. `dry_run`(기본 켬)은 빌드·서명 확인·트랙 조회까지만 합니다.
실제 업로드는 **Use workflow from**을 그 버전 태그(`vX.Y.Z`)로 골랐을 때만 됩니다 — 태그 실행이 실패했거나
승인 기한이 지나 다시 올릴 때 씁니다.

**이중 업로드 주의**: CI가 기본 경로이므로 아래 로컬 절차는 CI를 못 쓸 때만 씁니다. 같은 버전을 로컬에서 먼저
올렸다면 그 태그의 CI 실행은 승인하지 말고 거절합니다(승인해도 Play가 같은 versionCode를 거절해 실패로 끝납니다).

## 로컬로 빌드 (CI를 못 쓸 때)

업로드 키는 `D:\Project\homebase\.keystore\`에 있고(git 제외),
`G:\내 드라이브\모바일 앱 개발\Homebase\릴리즈 키스토어\`에 백업돼 있습니다. 같은 값이 GitHub 환경 `play`의
Secrets(`HOMEBASE_PLAY_UPLOAD_KEYSTORE_B64`·`_STORE_PASSWORD`·`_KEY_ALIAS`·`_KEY_PASSWORD`, `PLAY_SERVICE_ACCOUNT_JSON`)에
들어 있습니다 — 키를 바꾸면 Secrets와 `play-release.yml`의 `UPLOAD_CERT_SHA256`도 함께 바꿉니다.

1. 릴리스 태그(`vX.Y.Z`)가 가리키는 커밋을 체크아웃합니다. GitHub 릴리스와 같은 버전·같은 코드로 냅니다.
2. 서명 값 파일을 잠깐 `mobile/`에 복사합니다(`mobile/play-upload-signing.properties`는 gitignore됨).

   ```bash
   cp /d/Project/homebase/.keystore/play-upload-signing.properties mobile/
   ```

3. 테스트와 AAB 빌드:

   ```bash
   cd mobile && ./gradlew testPlayDebugUnitTest bundlePlayRelease -Phomebase.version=X.Y.Z -Phomebase.requirePlaySigning=true
   ```

   **메모리가 빠듯할 때**(다른 gradle 빌드·에뮬레이터와 겹칠 때): 커밋 여유가 수백 MB 아래로 떨어지면 AAPT2 데몬이
   죽어 `processPlayDebugResources` 등에서 실패합니다. 이때는 데몬을 띄우지 않고 일꾼을 하나로 줄여 다시 합니다.

   ```bash
   cd mobile && ./gradlew --no-daemon --max-workers=1 -Dorg.gradle.jvmargs=-Xmx1000m testPlayDebugUnitTest bundlePlayRelease -Phomebase.version=X.Y.Z -Phomebase.requirePlaySigning=true; echo "exit=$?"
   ```

   백그라운드로 돌리면 실패해도 종료 코드가 가려지므로, 끝난 뒤 반드시 종료 코드와 아래 산출물을 확인합니다.
   예전 빌드의 AAB가 남아 있어 실패를 놓치기 쉽습니다 — **AAB 수정 시각이 방금이고 versionCode가 이번 버전**이어야 합니다.

   ```bash
   aab=mobile/app/build/outputs/bundle/playRelease/app-play-release.aab
   ls -l --time-style=full-iso "$aab"
   unzip -p "$aab" base/manifest/AndroidManifest.xml | grep -aoE '[0-9]+\.[0-9]+\.[0-9]+' | head -1   # versionName = X.Y.Z
   ```

   versionCode는 versionName에서 `major*10000 + minor*100 + patch`로 정해집니다(예: 0.59.0 → 5900).

4. 서명 파일을 지웁니다: `rm mobile/play-upload-signing.properties`
5. 확인 — 업로드 키 인증서 SHA-256이 `a6aefcdda18a7f5935e63066d14cfa4e9b47db40048242f7bb87c12e33b44a5e`인지, 자체 업데이트 권한이 없는지:

   ```bash
   keytool -printcert -jarfile mobile/app/build/outputs/bundle/playRelease/app-play-release.aab
   ```

   Play 빌드의 병합 매니페스트에 `REQUEST_INSTALL_PACKAGES`·`UpdateCheckJobService`가 없어야 합니다(Mobile CI가 PR마다 검사).

6. 네이티브 디버그 기호 — 앱 코드는 Kotlin뿐이지만 의존성(Compose 등)에 네이티브 라이브러리(`.so`)가 들어 있어,
   기호가 없으면 Play Console이 출시마다 "네이티브 코드 포함, 디버그 기호 없음"을 경고하고 네이티브 비정상 종료·ANR을
   읽을 수 없습니다. release 빌드는 `ndk.debugSymbolLevel = "SYMBOL_TABLE"`(`mobile/app/build.gradle.kts`)이라
   AAB 안(`BUNDLE-METADATA/com.android.tools.build.debugsymbols/`)에 기호 표가 함께 들어가고, 업로드하면 Play가 알아서 씁니다.
   기호를 뽑는 데 **NDK가 필요합니다**(Android Studio → SDK Manager → SDK Tools → NDK (Side by side)). NDK가 없으면 빌드는
   되지만 `Unable to extract native debug metadata` 경고가 나고 기호가 빠집니다. 확인:

   ```bash
   unzip -l mobile/app/build/outputs/bundle/playRelease/app-play-release.aab | grep debugsymbols
   ```

   목록이 비어 있지 않으면 됩니다. 비어 있으면 NDK를 설치하고 2번부터 다시 합니다.

## 로컬로 업로드 (CI를 못 쓸 때)

전역 스킬 `play-publish`(API)로 올립니다: `-Track internal -AabPath <aab> -ExpectedUploadCertSha256 <콜론 형식 지문>`
다음 `-Track alpha -ExpectedVersionCode <code>`, 전후로 `-ShowTracks`. API가 안 될 때만 Play Console(개발자 계정
EL Studio) → Homebase → 테스트 및 출시 → 테스트 트랙 → 새 버전 만들기 → `app-play-release.aab` 업로드 →
출시 노트(ko-KR) → 저장/검토.

**지금 쓰는 테스트 트랙 모두를 같은 버전으로 맞춥니다**(내부 테스트와 비공개 테스트(Alpha)). 내부 테스트에만 올리면
비공개 테스트가 옛 버전에 머뭅니다 — 실제로 비공개 테스트가 0.40.0에 17버전 동안 남아 테스터가 공유 메뉴(0.49.0~)를
받지 못했습니다.

1. 내부 테스트에 새 버전을 올리고 출시합니다(검토 없이 바로 배포).
2. 내부 테스트 출시 화면의 **버전 승급** → 비공개 테스트(Alpha)로 같은 AAB를 올립니다(다시 업로드하지 않음).
   비공개 테스트는 매번 Google 검토를 거치므로 몇 시간~며칠 걸릴 수 있습니다. 승급은 검토를 기다리지 말고 바로 보냅니다.
3. 테스트 및 출시 → 개요(또는 각 트랙)에서 트랙마다 최신 버전이 이번 버전인지 확인합니다. 검토 중이면 "검토 중"으로
   보이는 것까지 확인하고, 다음 릴리스 때 거절·보류가 없는지 다시 봅니다.
4. 프로덕션은 공개하기로 정하기 전까지 올리지 않습니다.

- 첫 업로드 때 Play 앱 서명(Play App Signing)이 켜지고 Google이 앱 서명 키를 만듭니다. 업로드 키는 위 키입니다.
- Play에서 받은 앱의 페어링 링크가 '확인된 App Link'로 바로 열리려면, Play Console → 앱 무결성 → 앱 서명 키 인증서의
  SHA-256을 `jeiel85/jeiel85.github.io`의 `/.well-known/assetlinks.json`에 GitHub 키와 나란히 더합니다. 더하기 전에는
  설치 안내 페이지의 **앱에서 열기** 버튼(`homebase://pair`)으로 연결이 이어집니다.
- 개인 개발자 계정은 프로덕션 공개 전에 비공개 테스트(테스터 12명 이상, 14일)를 거쳐야 합니다.
- 업로드 키를 잃으면 Play Console에서 업로드 키 재설정을 요청합니다(앱 서명 키는 Google이 보관).
