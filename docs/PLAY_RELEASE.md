# Google Play 빌드 내보내기

Homebase 앱은 배포 채널마다 따로 빌드합니다(DECISIONS M-33).

| 채널 | Gradle flavor | 산출물 | 서명 | 업데이트 |
|---|---|---|---|---|
| GitHub Releases | `github` | `Homebase-Mobile.apk` (CI `release.yml`) | 릴리스 키 `homebase-release.jks` (M-12) | 앱 안 업데이터(하루 한 번 확인) |
| Google Play | `play` | `app-play-release.aab` (관리자 PC) | Play 업로드 키 `homebase-play-upload.jks` → Play가 앱 서명 키로 다시 서명 | Play 스토어 |

두 채널은 패키지 이름(`io.github.jeiel85.homebase`)과 versionCode 규칙(M-4)이 같지만 서명이 달라 **서로 덮어 설치할 수 없습니다.**
채널을 바꾸려면 앱을 지우고 다시 설치한 뒤 PC를 다시 연결해야 합니다.

## 빌드

Play 빌드는 CI가 아니라 관리자 PC에서 만듭니다. 업로드 키는 `D:\Project\homebase\.keystore\`에 있고(git 제외),
`G:\내 드라이브\모바일 앱 개발\Homebase\릴리즈 키스토어\`에 백업돼 있습니다.

1. 릴리스 태그(`vX.Y.Z`)가 가리키는 커밋을 체크아웃합니다. GitHub 릴리스와 같은 버전·같은 코드로 냅니다.
2. 서명 값 파일을 잠깐 `mobile/`에 복사합니다(`mobile/play-upload-signing.properties`는 gitignore됨).

   ```bash
   cp /d/Project/homebase/.keystore/play-upload-signing.properties mobile/
   ```

3. 테스트와 AAB 빌드:

   ```bash
   cd mobile && ./gradlew testPlayDebugUnitTest bundlePlayRelease -Phomebase.version=X.Y.Z -Phomebase.requirePlaySigning=true
   ```

4. 서명 파일을 지웁니다: `rm mobile/play-upload-signing.properties`
5. 확인 — 업로드 키 인증서 SHA-256이 `a6aefcdda18a7f5935e63066d14cfa4e9b47db40048242f7bb87c12e33b44a5e`인지, 자체 업데이트 권한이 없는지:

   ```bash
   keytool -printcert -jarfile mobile/app/build/outputs/bundle/playRelease/app-play-release.aab
   ```

   Play 빌드의 병합 매니페스트에 `REQUEST_INSTALL_PACKAGES`·`UpdateCheckJobService`가 없어야 합니다(Mobile CI가 PR마다 검사).

## 업로드

Play Console(개발자 계정 EL Studio) → Homebase → 테스트 및 출시 → 테스트 트랙(처음에는 내부 테스트) → 새 버전 만들기 →
`app-play-release.aab` 업로드 → 출시 노트(ko-KR) → 저장/검토.

- 첫 업로드 때 Play 앱 서명(Play App Signing)이 켜지고 Google이 앱 서명 키를 만듭니다. 업로드 키는 위 키입니다.
- Play에서 받은 앱의 페어링 링크가 '확인된 App Link'로 바로 열리려면, Play Console → 앱 무결성 → 앱 서명 키 인증서의
  SHA-256을 `jeiel85/jeiel85.github.io`의 `/.well-known/assetlinks.json`에 GitHub 키와 나란히 더합니다. 더하기 전에는
  설치 안내 페이지의 **앱에서 열기** 버튼(`homebase://pair`)으로 연결이 이어집니다.
- 개인 개발자 계정은 프로덕션 공개 전에 비공개 테스트(테스터 12명 이상, 14일)를 거쳐야 합니다.
- 업로드 키를 잃으면 Play Console에서 업로드 키 재설정을 요청합니다(앱 서명 키는 Google이 보관).
