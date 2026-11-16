package io.github.jeiel85.homebase.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 당겨서 새로고침의 직접 확인([UpdateRefresh.runNow])과 하루 1회 확인([UpdateRefresh.run])의 결과 분기.
 * 저장소는 메모리, GET은 가짜다 — 실제 네트워크·SharedPreferences·기기 SDK 없이 돈다.
 */
class UpdateRefreshTest {

    private class MemoryStore(var enabled: Boolean = true) : UpdateStore {
        var lastChecked = 0L
        var cached: String? = null
        var skipped = 0

        override fun isEnabled() = enabled
        override fun lastCheckedAt() = lastChecked
        override fun setLastCheckedAt(at: Long) {
            lastChecked = at
        }
        override fun cachedManifest() = cached
        override fun setCachedManifest(raw: String) {
            cached = raw
        }
        override fun skippedVersionCode() = skipped
        override fun setSkippedVersionCode(versionCode: Int) {
            skipped = versionCode
        }
    }

    private var fetches = 0

    private fun serving(raw: String?): () -> String? = {
        fetches++
        raw
    }

    private fun runNow(store: MemoryStore, raw: String?, now: Long = NOW, deviceSdk: Int = 30) =
        UpdateRefresh.runNow(store, serving(raw), now, installedVersionCode = INSTALLED, deviceSdk = deviceSdk)

    @Test
    fun runNow_offersANewerVersionAndRemembersTheAnswer() {
        val store = MemoryStore()
        val result = runNow(store, manifestJson(versionCode = INSTALLED + 1))

        assertTrue(result is UpdateRefresh.ManualResult.Offer)
        assertEquals(INSTALLED + 1, (result as UpdateRefresh.ManualResult.Offer).manifest.versionCode)
        // 배너·백그라운드 확인이 이 결과를 이어 쓴다.
        assertEquals(NOW, store.lastChecked)
        assertEquals(manifestJson(versionCode = INSTALLED + 1), store.cached)
    }

    @Test
    fun runNow_saysUpToDateForTheInstalledVersion() {
        assertEquals(UpdateRefresh.ManualResult.UpToDate, runNow(MemoryStore(), manifestJson(versionCode = INSTALLED)))
    }

    @Test
    fun runNow_doesNotCallANewerVersionTheDeviceCannotInstallUpToDate() {
        // #29: "최신 버전입니다"는 틀린 답이다 — 새 버전이 있지만 이 휴대폰의 Android가 낮다.
        val result = runNow(MemoryStore(), manifestJson(versionCode = INSTALLED + 1, minSdk = 31), deviceSdk = 30)

        assertTrue(result is UpdateRefresh.ManualResult.NeedsNewerAndroid)
        assertEquals(31, (result as UpdateRefresh.ManualResult.NeedsNewerAndroid).manifest.minSdk)
    }

    @Test
    fun runNow_releasesASkippedVersion() {
        // "이 버전 건너뛰기"를 되돌릴 다른 길이 없다. 직접 물은 사용자에게 새 버전을 숨기지 않는다.
        val store = MemoryStore().apply { skipped = INSTALLED + 1 }
        val result = runNow(store, manifestJson(versionCode = INSTALLED + 1))

        assertTrue(result is UpdateRefresh.ManualResult.Offer)
        assertEquals(0, store.skipped)
    }

    @Test
    fun runNow_failsWithoutTouchingTheLastGoodAnswer() {
        val store = MemoryStore().apply {
            lastChecked = NOW - 3_600_000
            cached = manifestJson(versionCode = INSTALLED)
        }

        assertEquals(UpdateRefresh.ManualResult.Failed, runNow(store, raw = null))
        assertEquals(UpdateRefresh.ManualResult.Failed, runNow(store, raw = "not json"))
        // 실패는 기록하지 않고, 깨진 응답은 캐시하지 않는다.
        assertEquals(NOW - 3_600_000, store.lastChecked)
        assertEquals(manifestJson(versionCode = INSTALLED), store.cached)
    }

    @Test
    fun runNow_staysOffTheNetworkWhenUpdateChecksAreOff() {
        assertEquals(UpdateRefresh.ManualResult.Disabled, runNow(MemoryStore(enabled = false), manifestJson(INSTALLED + 1)))
        assertEquals(0, fetches)
    }

    @Test
    fun runNow_answersARepeatedPullFromTheLastCheck() {
        // #29: 연달아 당기는 것이 그대로 GitHub 요청이 되지 않는다.
        val store = MemoryStore()
        runNow(store, manifestJson(versionCode = INSTALLED + 1), now = NOW)
        val again = runNow(store, manifestJson(versionCode = INSTALLED + 2), now = NOW + 30_000)

        assertEquals(1, fetches)
        assertEquals(INSTALLED + 1, (again as UpdateRefresh.ManualResult.Offer).manifest.versionCode)

        // 1분이 지나면 다시 묻는다.
        val later = runNow(store, manifestJson(versionCode = INSTALLED + 2), now = NOW + UpdateDecision.MANUAL_CHECK_MIN_INTERVAL_MILLIS)
        assertEquals(2, fetches)
        assertEquals(INSTALLED + 2, (later as UpdateRefresh.ManualResult.Offer).manifest.versionCode)
    }

    @Test
    fun runNow_asksAgainRightAfterAFailure() {
        // 실패는 확인 시각을 남기지 않으므로, 네트워크를 고치고 바로 다시 당기면 다시 묻는다.
        val store = MemoryStore()
        runNow(store, raw = null, now = NOW)
        runNow(store, manifestJson(versionCode = INSTALLED + 1), now = NOW + 5_000)
        assertEquals(2, fetches)
    }

    @Test
    fun run_checksOnceADayAndDecidesFromTheCache() {
        val store = MemoryStore()
        val first = UpdateRefresh.run(store, serving(manifestJson(INSTALLED + 1)), NOW, installedVersionCode = INSTALLED, deviceSdk = 30)
        val sameDay = UpdateRefresh.run(store, serving(manifestJson(INSTALLED + 2)), NOW + 3_600_000, installedVersionCode = INSTALLED, deviceSdk = 30)

        assertEquals(INSTALLED + 1, first?.versionCode)
        assertEquals(INSTALLED + 1, sameDay?.versionCode)
        assertEquals(1, fetches)
    }

    @Test
    fun run_staysQuietForASkippedVersion() {
        val store = MemoryStore().apply { skipped = INSTALLED + 1 }
        assertNull(UpdateRefresh.run(store, serving(manifestJson(INSTALLED + 1)), NOW, installedVersionCode = INSTALLED, deviceSdk = 30))
    }

    private fun manifestJson(versionCode: Int, minSdk: Int = 26) =
        """{"versionCode":$versionCode,"versionName":"0.31.$versionCode","apkUrl":"https://example.invalid/app.apk",""" +
            """"apkSizeBytes":10,"sha256":"ab","minSdk":$minSdk,"releaseNotesUrl":"https://example.invalid/notes"}"""

    private companion object {
        const val NOW = 1_800_000_000_000L
        const val INSTALLED = 3000
    }
}
