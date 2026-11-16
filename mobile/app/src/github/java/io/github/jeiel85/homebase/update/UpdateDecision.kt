package io.github.jeiel85.homebase.update

import java.util.concurrent.TimeUnit

/**
 * "확인할 때인가"와 "알릴 만한가"를 판단한다. 순수 함수만 두는 이유는 이 두 판단이 업데이트
 * 기능에서 **틀리면 가장 사용자에게 거슬리는 부분**이기 때문이다 — 너무 자주 확인하면 배경
 * 트래픽이 되고, 판단이 느슨하면 이미 최신인 사용자에게 배너를 띄운다. 네트워크와 저장소를
 * 섞지 않아야 그 규칙을 테스트로 고정할 수 있다.
 *
 * 상태(마지막 확인 시각, 건너뛴 버전)는 인자로 받는다. 저장은 호출부의 몫이다.
 */
internal object UpdateDecision {

    val CHECK_INTERVAL_MILLIS: Long = TimeUnit.DAYS.toMillis(1)

    /**
     * Input : 마지막으로 확인에 성공한 시각, 현재 시각(둘 다 epoch millis), 확인 간격
     * Output: 지금 네트워크 확인을 해도 되는지
     *
     * 핵심 로직: 마지막 확인이 없으면(0) 확인한다. 그 외에는 간격이 지나야 한다.
     *
     * 미래 시각을 받은 경우도 확인을 허용한다 — 기기 시계가 앞당겨졌다가 돌아오면
     * `now - lastCheckedAt`이 음수가 되어 **영원히 확인하지 않는 상태**에 갇힐 수 있다.
     * 사용자가 시계를 고쳤을 때 기능이 조용히 죽어 있는 것보다 한 번 더 확인하는 편이 낫다.
     */
    fun shouldCheckNow(
        lastCheckedAt: Long,
        now: Long,
        interval: Long = CHECK_INTERVAL_MILLIS,
    ): Boolean {
        if (lastCheckedAt <= 0L) return true
        if (lastCheckedAt > now) return true
        return now - lastCheckedAt >= interval
    }

    /**
     * Input : 받아온 manifest, 설치된 versionCode, 사용자가 건너뛴 versionCode(없으면 0),
     *         기기의 SDK 레벨
     * Output: 배너를 띄울 만한 새 버전인지
     *
     * 핵심 로직: `versionCode` **정수 비교**만 쓴다. `versionName` 문자열 비교는
     * `2.9.0`을 `2.10.0`보다 크게 만든다.
     *
     * 같거나 낮은 versionCode에서 멈추는 것은 개발 빌드를 위한 것이기도 하다 — 로컬에서 올린
     * 버전을 쓰는 사람에게 "구버전으로 업데이트하세요"를 띄우지 않는다.
     *
     * `minSdk` 확인은 설치가 실패할 것을 미리 아는 경우를 거른다. 받으라고 안내한 뒤
     * 시스템이 거부하는 흐름은 사용자가 원인을 알 수 없다.
     */
    fun shouldOffer(
        manifest: UpdateManifest,
        installedVersionCode: Int,
        skippedVersionCode: Int,
        deviceSdk: Int,
    ): Boolean {
        if (manifest.versionCode <= installedVersionCode) return false
        if (manifest.versionCode == skippedVersionCode) return false
        if (deviceSdk < manifest.minSdk) return false
        return true
    }

    /**
     * Input : 알릴 만하다고 판정된 versionCode, 이미 알림을 띄운 versionCode(없으면 0)
     * Output: 지금 Android 알림을 띄워야 하는지
     *
     * 핵심 로직: **한 버전에 알림은 한 번**이다. 백그라운드 확인은 하루에도 여러 번 돌 수 있고
     * 캐시된 manifest로 같은 판정을 반복하므로, 이 규칙이 없으면 같은 새 버전을 매번 다시 알린다.
     * 사용자가 알림을 지웠다면 그것도 답이다 — 앱 안 배너는 계속 남는다.
     */
    fun shouldNotify(offeredVersionCode: Int, notifiedVersionCode: Int): Boolean =
        offeredVersionCode != notifiedVersionCode

    /**
     * Input : 백그라운드 확인(GET)이 끝난 **뒤의** 상태 — 잡이 중단됐는지, "업데이트 확인"이 아직
     *         켜져 있는지, 알릴 versionCode, 이미 알린 versionCode, 그리고 알림이 실제로 보일 수 있는지
     *         (권한·앱 알림이 켜져 있고 `hb.app.update` 채널이 IMPORTANCE_NONE이 아닌지)
     * Output: 지금 알림을 게시해도 되는지. 게시에 성공했을 때만 그 버전을 "알렸다"로 기록한다.
     *
     * 핵심 로직: 확인하는 사이 사용자가 설정을 껐거나 잡이 멈췄으면 게시하지 않는다 — 그 선택을
     * 되돌리게 된다. 채널이 꺼져 있으면 Android가 예외 없이 조용히 버리므로, 게시를 성공으로 치면
     * 그 버전을 "알렸다"고 기억해 채널을 다시 켜도 영영 알리지 않는다. 그래서 보일 수 없을 때도 false다.
     * [notificationVisible]은 앞 조건을 모두 통과했을 때만 부른다(채널 생성 같은 부수 효과가 있다).
     */
    fun shouldPostNotification(
        stopped: Boolean,
        enabled: Boolean,
        offeredVersionCode: Int,
        notifiedVersionCode: Int,
        notificationVisible: () -> Boolean,
    ): Boolean =
        !stopped && enabled && shouldNotify(offeredVersionCode, notifiedVersionCode) && notificationVisible()

    /** 당겨서 새로고침을 연달아 해도 GitHub에 다시 묻지 않는 간격. */
    val MANUAL_CHECK_MIN_INTERVAL_MILLIS: Long = TimeUnit.SECONDS.toMillis(60)

    /**
     * Input : 마지막으로 확인에 성공한 시각, 현재 시각, 최소 간격
     * Output: 직접 확인([UpdateRefresh.runNow])이 네트워크 없이 방금 받은 결과를 다시 써도 되는지
     *
     * 핵심 로직: 반복해서 당기는 것이 그대로 외부 요청이 되지 않게 한다. 시각이 미래면(시계가
     * 되돌아감) 신선하다고 보지 않는다 — [shouldCheckNow]와 같은 이유로, 갇히느니 한 번 더 묻는다.
     */
    fun isManualCheckFresh(
        lastCheckedAt: Long,
        now: Long,
        interval: Long = MANUAL_CHECK_MIN_INTERVAL_MILLIS,
    ): Boolean = lastCheckedAt in 1L..now && now - lastCheckedAt < interval

    /** 설치 권한을 주러 설정에 다녀오는 동안의 "이어서 설치" 요청이 유효한 시간. */
    val RESUME_INSTALL_WINDOW_MILLIS: Long = TimeUnit.MINUTES.toMillis(10)

    /**
     * Input : "업데이트"를 눌렀다가 권한 때문에 설정으로 보낸 versionCode와 그 시각, 지금 배너가 알리는
     *         versionCode, 현재 시각, 지금 설치 권한이 있는지
     * Output: 사용자가 다시 누르지 않아도 받기를 이어 가야 하는지
     *
     * 핵심 로직: 권한을 바꾸면 앱 프로세스를 다시 시작하는 기기가 있어 대화상자가 사라진다. 그때도
     * 사용자가 요청한 그 버전을 이어서 받는다. 오래된 요청(다음 날 앱을 열었을 때)이나 다른 버전은
     * 이어 가지 않는다 — 사용자가 고르지 않은 시점에 받기를 시작하지 않기 위해서다.
     */
    fun shouldResumeInstall(
        pendingVersionCode: Int,
        pendingAt: Long,
        offeredVersionCode: Int,
        now: Long,
        canInstall: Boolean,
        window: Long = RESUME_INSTALL_WINDOW_MILLIS,
    ): Boolean =
        canInstall &&
            pendingVersionCode != 0 &&
            pendingVersionCode == offeredVersionCode &&
            pendingAt in 1L..now &&
            now - pendingAt <= window

    /**
     * Input : 설치된 앱 서명 인증서들의 SHA-256(16진수), 업데이트 APK를 서명하는 인증서의 SHA-256
     *         (`BuildConfig.UPDATE_SIGNER_SHA256`, DECISIONS M-12 — 비어 있으면 검사하지 않음)
     * Output: 이 설치본 위에 업데이트를 덮어 설치할 수 있는지
     *
     * 핵심 로직: 다른 키(디버그 키 등)로 서명된 설치본은 릴리스 APK로 업데이트할 수 없다 — 시스템이
     * 설치 확인까지 다 띄운 뒤에 거부한다. 미리 알면 받기 전에 안내할 수 있다. 서명을 읽지 못했으면
     * (빈 목록) 막지 않는다 — 잘못된 사전 검사로 정상 업데이트를 막는 쪽이 더 나쁘다.
     */
    fun canUpdateInPlace(installedSignerSha256: List<String>, updateSignerSha256: String): Boolean =
        updateSignerSha256.isBlank() ||
            installedSignerSha256.isEmpty() ||
            installedSignerSha256.any { it.equals(updateSignerSha256, ignoreCase = true) }
}
