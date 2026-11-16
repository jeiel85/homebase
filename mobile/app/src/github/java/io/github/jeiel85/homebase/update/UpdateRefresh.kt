package io.github.jeiel85.homebase.update

import android.os.Build
import io.github.jeiel85.homebase.BuildConfig

/**
 * 목록 화면의 배너([UpdateSurface.Banner])와 백그라운드 확인([UpdateCheckJobService])이 함께 쓰는
 * 판단 한 벌. 두 곳이 같은 캐시·같은 하루 1회 규칙을 쓰므로, 화면을 열었든 백그라운드에서 돌았든
 * 네트워크 확인은 하루에 한 번이다.
 *
 * 저장소([UpdateStore])·GET([fetch])·시각·설치된 버전·기기 SDK는 인자로 받는다(기본값이 실제 값).
 * 그래서 결과 분기를 JVM 단위 테스트(`UpdateRefreshTest`)로 고정할 수 있다.
 */
internal object UpdateRefresh {

    /** 실제 GET. 테스트는 이 자리에 가짜를 넘긴다. */
    private val fetch: () -> String? = { UpdateChecker().fetchRaw() }

    /**
     * Input : [UpdateStore], 그리고 테스트용 GET·현재 시각·설치된 versionCode·기기 SDK
     * Output: 지금 알릴 만한 [UpdateManifest], 아니면 null
     *
     * 핵심 로직: 네트워크는 하루 1회만 타고, 판단은 캐시된 원문으로 매번 다시 한다.
     *
     * **온전하게 파싱되는 응답만 저장하고, 그때만 확인 시각을 찍는다.** 실패로 하루를 소모하면
     * 잠깐 오프라인이었던 사용자가 다음 날까지 확인하지 못하고, 깨진 응답을 캐시하면 그 하루
     * 동안 매 실행에서 같은 쓰레기를 파싱하고 버린다.
     *
     * 블로킹 네트워크 호출이 있으므로 메인 스레드가 아닌 곳에서 부른다.
     */
    fun run(
        preferences: UpdateStore,
        fetchRaw: () -> String? = fetch,
        now: Long = System.currentTimeMillis(),
        installedVersionCode: Int = BuildConfig.VERSION_CODE,
        deviceSdk: Int = Build.VERSION.SDK_INT,
    ): UpdateManifest? {
        if (!preferences.isEnabled()) return null

        if (UpdateDecision.shouldCheckNow(preferences.lastCheckedAt(), now)) {
            fetchAndStore(preferences, fetchRaw, now)
        }

        val manifest = preferences.cachedManifest()?.let(UpdateManifestParser::parse) ?: return null
        return manifest.takeIf { shouldOffer(it, preferences, installedVersionCode, deviceSdk) }
    }

    /** 사용자가 직접 요청한 확인([runNow])의 결과. 화면이 무엇을 말할지 고르는 데 쓴다. */
    sealed interface ManualResult {
        data class Offer(val manifest: UpdateManifest) : ManualResult

        /** 더 새 버전이 있지만 이 휴대폰의 Android가 그 버전의 `minSdk`보다 낮아 설치할 수 없다. */
        data class NeedsNewerAndroid(val manifest: UpdateManifest) : ManualResult
        data object UpToDate : ManualResult
        data object Failed : ManualResult
        data object Disabled : ManualResult
    }

    /**
     * Input : [UpdateStore], 그리고 테스트용 GET·현재 시각·설치된 versionCode·기기 SDK
     * Output: [ManualResult]
     *
     * 핵심 로직: 목록을 아래로 당겼을 때 부른다. 사용자가 **지금** 확인해 달라고 한 것이므로
     * 하루 1회 규칙을 건너뛰고 바로 GET 한다. 다만 직전 성공에서 1분([UpdateDecision.isManualCheckFresh])이
     * 지나지 않았으면 다시 묻지 않고 그 결과(캐시)로 답한다 — 연달아 당기는 것이 그대로 GitHub
     * 요청이 되지 않게. 결과 저장 규칙은 [run]과 같다(온전히 파싱되는 응답만 캐시하고 그때만 확인
     * 시각을 찍는다) — 그래서 그 뒤의 배너·백그라운드 확인은 이 결과를 그대로 이어 쓴다.
     *
     * 건너뛴 버전은 여기서 풀어 준다. "이 버전 건너뛰기"를 되돌릴 다른 길이 없고, 직접 확인을
     * 요청한 사용자에게 "최신입니다"라고 답하면서 실제로는 새 버전을 숨기는 것은 틀린 답이다.
     * 알림은 버전당 한 번 규칙([UpdateDecision.shouldNotify])이 그대로라 다시 울리지 않는다.
     * 같은 이유로 새 버전이 이 휴대폰의 Android로는 설치할 수 없을 때도 "최신"이라 하지 않는다.
     *
     * "업데이트 확인"이 꺼져 있으면 네트워크를 타지 않는다 — 그 설정은 GitHub로 나가는 요청
     * 자체에 대한 동의다.
     *
     * 블로킹 네트워크 호출이 있으므로 메인 스레드가 아닌 곳에서 부른다.
     */
    fun runNow(
        preferences: UpdateStore,
        fetchRaw: () -> String? = fetch,
        now: Long = System.currentTimeMillis(),
        installedVersionCode: Int = BuildConfig.VERSION_CODE,
        deviceSdk: Int = Build.VERSION.SDK_INT,
    ): ManualResult {
        if (!preferences.isEnabled()) return ManualResult.Disabled
        val recent = if (UpdateDecision.isManualCheckFresh(preferences.lastCheckedAt(), now)) {
            preferences.cachedManifest()?.let(UpdateManifestParser::parse)
        } else {
            null
        }
        val manifest = recent ?: fetchAndStore(preferences, fetchRaw, now) ?: return ManualResult.Failed
        if (preferences.skippedVersionCode() != 0) preferences.setSkippedVersionCode(0)
        return when {
            shouldOffer(manifest, preferences, installedVersionCode, deviceSdk) -> ManualResult.Offer(manifest)
            // 건너뛰기는 방금 풀었으니, 더 새 버전인데 알리지 않는 이유는 minSdk뿐이다.
            manifest.versionCode > installedVersionCode -> ManualResult.NeedsNewerAndroid(manifest)
            else -> ManualResult.UpToDate
        }
    }

    /** GET 한 번. 온전히 파싱될 때만 원문을 캐시하고 확인 시각을 찍는다. 실패는 null. */
    private fun fetchAndStore(preferences: UpdateStore, fetchRaw: () -> String?, now: Long): UpdateManifest? {
        val raw = fetchRaw() ?: return null
        val manifest = UpdateManifestParser.parse(raw) ?: return null
        preferences.setCachedManifest(raw)
        preferences.setLastCheckedAt(now)
        return manifest
    }

    private fun shouldOffer(
        manifest: UpdateManifest,
        preferences: UpdateStore,
        installedVersionCode: Int,
        deviceSdk: Int,
    ): Boolean =
        UpdateDecision.shouldOffer(
            manifest = manifest,
            installedVersionCode = installedVersionCode,
            skippedVersionCode = preferences.skippedVersionCode(),
            deviceSdk = deviceSdk,
        )
}
