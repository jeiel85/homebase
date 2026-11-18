package io.github.jeiel85.homebase.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.TimeUnit

/**
 * 확인 주기와 배너 판정 규칙.
 *
 * 순수 함수만 다루므로 Robolectric이 필요 없다 — `UpdateManifestParserTest`와 달리 `org.json`을
 * 건드리지 않는다.
 */
class UpdateDecisionTest {

    @Test
    fun shouldCheckNow_checksOnTheFirstRun() {
        assertTrue(UpdateDecision.shouldCheckNow(lastCheckedAt = 0L, now = NOW))
    }

    @Test
    fun shouldCheckNow_waitsOutTheInterval() {
        val anHourAgo = NOW - TimeUnit.HOURS.toMillis(1)
        assertFalse(UpdateDecision.shouldCheckNow(lastCheckedAt = anHourAgo, now = NOW))

        val aDayAgo = NOW - UpdateDecision.CHECK_INTERVAL_MILLIS
        assertTrue(UpdateDecision.shouldCheckNow(lastCheckedAt = aDayAgo, now = NOW))
    }

    @Test
    fun shouldCheckNow_recoversFromAClockThatWentBackwards() {
        // 기기 시계가 앞당겨졌다가 돌아오면 마지막 확인 시각이 미래로 남는다. 그 상태를 "아직
        // 멀었다"로 읽으면 기능이 조용히 죽고, 사용자는 업데이트가 없다고 믿는다.
        val tomorrow = NOW + TimeUnit.DAYS.toMillis(1)
        assertTrue(UpdateDecision.shouldCheckNow(lastCheckedAt = tomorrow, now = NOW))
    }

    @Test
    fun shouldOffer_offersANewerVersion() {
        assertTrue(
            UpdateDecision.shouldOffer(
                manifest = manifest(versionCode = 145),
                installedVersionCode = 144,
                skippedVersionCode = 0,
                deviceSdk = 35,
            )
        )
    }

    @Test
    fun shouldOffer_staysQuietOnTheSameOrAnOlderVersion() {
        // versionName 문자열 비교였다면 2.9.0 > 2.10.0 같은 오판이 난다. 정수만 본다.
        assertFalse(
            UpdateDecision.shouldOffer(manifest(versionCode = 144), installedVersionCode = 144, skippedVersionCode = 0, deviceSdk = 35)
        )
        // 로컬에서 올린 개발 빌드를 쓰는 사람에게 "구버전으로 업데이트하세요"를 띄우지 않는다.
        assertFalse(
            UpdateDecision.shouldOffer(manifest(versionCode = 143), installedVersionCode = 144, skippedVersionCode = 0, deviceSdk = 35)
        )
    }

    @Test
    fun shouldOffer_respectsASkippedVersion() {
        assertFalse(
            UpdateDecision.shouldOffer(manifest(versionCode = 145), installedVersionCode = 144, skippedVersionCode = 145, deviceSdk = 35)
        )
        // 건너뛴 것은 그 버전 하나뿐이다. 다음 버전은 다시 알린다.
        assertTrue(
            UpdateDecision.shouldOffer(manifest(versionCode = 146), installedVersionCode = 144, skippedVersionCode = 145, deviceSdk = 35)
        )
    }

    @Test
    fun shouldNotify_notifiesEachVersionOnce() {
        // 백그라운드 확인은 하루에도 여러 번 같은 캐시로 같은 판정을 한다. 이미 알린 버전을 다시
        // 알리면 사용자가 지운 알림이 계속 되살아난다.
        assertTrue(UpdateDecision.shouldNotify(offeredVersionCode = 1800, notifiedVersionCode = 0))
        assertFalse(UpdateDecision.shouldNotify(offeredVersionCode = 1800, notifiedVersionCode = 1800))
        // 그 사이 더 새 버전이 나오면 다시 알린다.
        assertTrue(UpdateDecision.shouldNotify(offeredVersionCode = 1801, notifiedVersionCode = 1800))
    }

    @Test
    fun shouldOffer_staysQuietWhenTheDeviceIsTooOld() {
        // 받으라고 안내한 뒤 시스템이 설치를 거부하면 사용자는 원인을 알 수 없다.
        assertFalse(
            UpdateDecision.shouldOffer(manifest(versionCode = 145, minSdk = 30), installedVersionCode = 144, skippedVersionCode = 0, deviceSdk = 26)
        )
        assertTrue(
            UpdateDecision.shouldOffer(manifest(versionCode = 145, minSdk = 26), installedVersionCode = 144, skippedVersionCode = 0, deviceSdk = 26)
        )
    }

    @Test
    fun shouldPostNotification_postsANewVersionOnceWhenItCanBeSeen() {
        assertTrue(post())
        // 이미 알린 버전은 다시 울리지 않는다.
        assertFalse(post(notified = 1800))
    }

    @Test
    fun shouldPostNotification_respectsAStopOrATurnOffDuringTheCheck() {
        // GET 하는 사이 잡이 멈췄거나 사용자가 "업데이트 확인"을 껐다 — 알리면 그 선택을 되돌린다.
        assertFalse(post(stopped = true))
        assertFalse(post(enabled = false))
    }

    @Test
    fun shouldPostNotification_skipsAChannelTheUserTurnedOff() {
        // hb.app.update가 IMPORTANCE_NONE이면 Android가 조용히 버린다. 게시를 성공으로 치면 그 버전을
        // "알렸다"고 기억해, 채널을 다시 켜도 영영 알리지 않는다.
        assertFalse(post(visible = false))
    }

    @Test
    fun shouldPostNotification_looksAtTheChannelOnlyWhenEverythingElseSaysYes() {
        // 채널 확인은 채널을 만드는 부수 효과가 있다 — 끈 사용자에게 채널을 새로 만들지 않는다.
        var asked = 0
        val countAsk = { asked++; true }
        UpdateDecision.shouldPostNotification(stopped = true, enabled = true, offeredVersionCode = 1800, notifiedVersionCode = 0, notificationVisible = countAsk)
        UpdateDecision.shouldPostNotification(stopped = false, enabled = false, offeredVersionCode = 1800, notifiedVersionCode = 0, notificationVisible = countAsk)
        UpdateDecision.shouldPostNotification(stopped = false, enabled = true, offeredVersionCode = 1800, notifiedVersionCode = 1800, notificationVisible = countAsk)
        assertEquals(0, asked)
    }

    @Test
    fun isManualCheckFresh_reusesAResultForAMinute() {
        assertTrue(UpdateDecision.isManualCheckFresh(lastCheckedAt = NOW - 1_000, now = NOW))
        assertFalse(UpdateDecision.isManualCheckFresh(lastCheckedAt = NOW - UpdateDecision.MANUAL_CHECK_MIN_INTERVAL_MILLIS, now = NOW))
        // 확인한 적 없음, 또는 시계가 되돌아가 미래 시각 — 다시 묻는다.
        assertFalse(UpdateDecision.isManualCheckFresh(lastCheckedAt = 0L, now = NOW))
        assertFalse(UpdateDecision.isManualCheckFresh(lastCheckedAt = NOW + 1_000, now = NOW))
    }

    @Test
    fun shouldResumeInstall_carriesOnRightAfterThePermissionWasGranted() {
        assertTrue(resume())
        // 권한을 주지 않고 돌아왔다.
        assertFalse(resume(canInstall = false))
        // 설정에 다녀온 버전과 지금 알리는 버전이 다르다.
        assertFalse(resume(offered = 1801))
        // 표시가 없다.
        assertFalse(resume(pending = 0))
        // 오래된 요청 — 사용자가 고르지 않은 때에 받기를 시작하지 않는다.
        assertFalse(resume(at = NOW - UpdateDecision.RESUME_INSTALL_WINDOW_MILLIS - 1))
        assertFalse(resume(at = NOW + 60_000))
    }

    @Test
    fun canUpdateInPlace_tellsADebugSignedInstallApart() {
        val release = "4d9cbd4d68cd2ee792fd1f506cbfae9c2313186b668a5ca34d9020f5d23ef865"
        assertTrue(UpdateDecision.canUpdateInPlace(listOf(release), release))
        assertTrue(UpdateDecision.canUpdateInPlace(listOf(release.uppercase()), release))
        // 디버그 키로 서명된 설치본: 릴리스 APK는 그 위에 설치되지 않는다.
        assertFalse(UpdateDecision.canUpdateInPlace(listOf("ab".repeat(32)), release))
        // 키를 교체한 이력 안에 릴리스 키가 있으면 된다.
        assertTrue(UpdateDecision.canUpdateInPlace(listOf("ab".repeat(32), release), release))
        // 서명을 읽지 못했거나(빈 목록) 검사하지 않는 빌드(빈 기대값)는 막지 않는다.
        assertTrue(UpdateDecision.canUpdateInPlace(emptyList(), release))
        assertTrue(UpdateDecision.canUpdateInPlace(listOf("ab".repeat(32)), ""))
    }

    @Test
    fun shouldInstallSilently_installsWhileTheAppIsOutOfSight() {
        assertTrue(silent())
    }

    @Test
    fun shouldInstallSilently_neverReplacesAnAppOnScreen() {
        // 설치가 끝나면 프로세스가 교체되며 쓰던 화면이 말없이 닫힌다 — 그때는 배너와 버튼이 맡는다.
        assertFalse(silent(visible = true))
    }

    @Test
    fun shouldInstallSilently_followsTheSettingAndWhatTheSystemAllows() {
        assertFalse(silent(auto = false))
        // Android 11 이하에는 확인 없는 설치가 없다.
        assertFalse(silent(sdk = 30))
        assertTrue(silent(sdk = UpdateDecision.SILENT_INSTALL_MIN_SDK))
        // 디버그 키 설치본은 릴리스 APK로 덮어 설치할 수 없다.
        assertFalse(silent(inPlace = false))
        // '알 수 없는 앱 설치' 권한이 없으면 세션이 확인을 묻는다 — 받지 않는다.
        assertFalse(silent(canInstall = false))
    }

    @Test
    fun shouldInstallSilently_doesNotDownloadAgainWhatTheSystemWantsConfirmed() {
        assertFalse(silent(needsConfirm = 1800))
        // 확인을 요구받은 것은 지난 버전이다 — 이번 버전은 (그 확인으로 설치자가 바뀌었을 수 있으니) 다시 해 본다.
        assertTrue(silent(needsConfirm = 1700))
    }

    @Test
    fun shouldInstallSilently_givesUpOnAVersionAfterRepeatedFailures() {
        assertTrue(silent(failedVersion = 1800, failures = UpdateDecision.MAX_SILENT_ATTEMPTS - 1))
        assertFalse(silent(failedVersion = 1800, failures = UpdateDecision.MAX_SILENT_ATTEMPTS))
        // 실패 횟수는 그 버전의 것이다 — 다음 버전은 처음부터.
        assertTrue(silent(failedVersion = 1700, failures = UpdateDecision.MAX_SILENT_ATTEMPTS))
    }

    private fun silent(
        auto: Boolean = true,
        sdk: Int = 34,
        visible: Boolean = false,
        inPlace: Boolean = true,
        canInstall: Boolean = true,
        needsConfirm: Int = 0,
        failedVersion: Int = 0,
        failures: Int = 0,
    ) = UpdateDecision.shouldInstallSilently(
        autoInstall = auto,
        deviceSdk = sdk,
        appVisible = visible,
        canUpdateInPlace = inPlace,
        canRequestInstalls = canInstall,
        offeredVersionCode = 1800,
        needsConfirmVersionCode = needsConfirm,
        failedVersionCode = failedVersion,
        failedAttempts = failures,
    )

    private fun post(
        stopped: Boolean = false,
        enabled: Boolean = true,
        notified: Int = 0,
        visible: Boolean = true,
    ) = UpdateDecision.shouldPostNotification(
        stopped = stopped,
        enabled = enabled,
        offeredVersionCode = 1800,
        notifiedVersionCode = notified,
        notificationVisible = { visible },
    )

    private fun resume(
        pending: Int = 1800,
        at: Long = NOW - 30_000,
        offered: Int = 1800,
        canInstall: Boolean = true,
    ) = UpdateDecision.shouldResumeInstall(
        pendingVersionCode = pending,
        pendingAt = at,
        offeredVersionCode = offered,
        now = NOW,
        canInstall = canInstall,
    )

    private fun manifest(versionCode: Int, minSdk: Int = 26) = UpdateManifest(
        versionCode = versionCode,
        versionName = "test",
        apkUrl = "https://example.invalid/markleaf.apk",
        apkSizeBytes = 1,
        sha256 = "0",
        minSdk = minSdk,
        releaseNotesUrl = "https://example.invalid/notes",
    )

    private companion object {
        const val NOW = 1_800_000_000_000L
    }
}
